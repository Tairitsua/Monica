using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.ApplicationRoles;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Models;
using Monica.DataChannel.Pipeline;
using Monica.Modules;

namespace Monica.DataChannel.Services;

/// <summary>Owns setup admission, channel identity and endpoint lifetimes for one host.</summary>
internal sealed class DataChannelRuntime : IDataChannelRegistrar, IDataChannelManager, IDataChannelLifecycle, IRoleLifecycleParticipant
{
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly IServiceProvider _services;
    private readonly MonicaApplication _application;
    private readonly IRoleHealthStateProvider? _roleHealth;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DataChannelRuntime> _logger;
    private readonly int _initThreadCount;
    private readonly Dictionary<string, SetupEntry> _setups;
    private readonly Dictionary<string, SetupEntry> _channelOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DataChannel> _channels = new(StringComparer.Ordinal);
    private bool _configured;
    private bool _stopping;
    private Task? _shutdown;
    private readonly Dictionary<string, Lazy<Task>> _roleRetirements = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdownCancellation;
    private readonly CancellationToken _shutdownToken;

    public DataChannelRuntime(IServiceProvider services, MonicaApplication application,
        IEnumerable<DataChannelSetupRegistration> registrations, IHostApplicationLifetime lifetime,
        IOptions<ModuleDataChannelOption> options, ILogger<DataChannelRuntime> logger)
    {
        _services = services;
        _application = application;
        _lifetime = lifetime;
        _logger = logger;
        _initThreadCount = options.Value.InitThreadCount;
        _shutdownCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _shutdownToken = _shutdownCancellation.Token;
        _roleHealth = services.GetService<IRoleHealthStateProvider>();
        _setups = registrations.Select(registration => new SetupEntry(registration,
            _roleHealth?.GetRoleId(registration.ImplementationType, registration.RegistrationOwner))).ToDictionary(entry => entry.Id, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public void Add(string id, Action<ChannelPipelineBuilder> configure, string? groupId = null) =>
        throw new InvalidOperationException("Data-channel registration is confined to the selected setup callback.");

    internal void ConfigureApplication(IApplicationBuilder app)
    {
        if (_configured) throw new InvalidOperationException("Data-channel pipelines have already been configured for this host.");
        var automatic = _setups.Values.Where(entry => entry.Registration.Mode == ActivationMode.Automatic).ToArray();
        try
        {
            // Detect duplicate identities across every automatic declaration before building any endpoint.
            foreach (var entry in automatic) ConfigureEntry(entry, () => Declare(entry));
            foreach (var entry in automatic) ConfigureEntry(entry, () => MaterializeAsync(entry, atStartup: true).GetAwaiter().GetResult());
            // These callbacks can mutate the host and cannot be rolled back. Only committed setups reach them.
            foreach (var component in ApplicationComponents()) component.ConfigApplicationBuilder(app);
            _configured = true;
        }
        catch (Exception original)
        {
            try { ShutdownAsync().GetAwaiter().GetResult(); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }

    internal void ConfigureEndpoints(IApplicationBuilder app)
    {
        foreach (var component in ApplicationComponents()) component.ConfigEndpoints(app);
    }

    private IEnumerable<IApplicationBuilderConfigurable> ApplicationComponents() =>
        FetchAll().SelectMany(channel => channel.Pipe.GetComponents()).OfType<IApplicationBuilderConfigurable>();

    private bool IsQuarantined(SetupEntry entry) => entry.RoleId is { } roleId && _roleHealth!.IsRoleFaulted(roleId);

    private void ConfigureEntry(SetupEntry entry, Action configure)
    {
        if (IsQuarantined(entry)) return;
        try { configure(); }
        catch (Exception exception) when (entry.RoleId is not null && exception is not
            (ChannelIdentityConflictException or PipelineRollbackException or OperationCanceledException))
        {
            var fault = RoleFaultRecord.ForResource(RoleFaultStage.PipelineSetup, entry.Registration.ImplementationType);
            var coordinator = _services.GetService<IRoleFaultCoordinator>();
            if (coordinator is null) throw;
            // No channel has started yet. Roll back this owner before host callbacks or deferred participant cleanup.
            OnRoleQuarantinedAsync(entry.RoleId, CancellationToken.None).GetAwaiter().GetResult();
            if (!coordinator.TryIsolateStartupFault(entry.RoleId, fault)) throw;
            _logger.LogError(exception, "Quarantined role {RoleId} after data-channel setup {SetupId} failed.", entry.RoleId, entry.Id);
        }
    }

    /// <inheritdoc />
    public async Task OnRoleQuarantinedAsync(string roleId, CancellationToken cancellationToken)
    {
        Lazy<Task> retirement;
        await _lifecycle.WaitAsync();
        try
        {
            if (!_roleRetirements.TryGetValue(roleId, out retirement!))
            {
                var retired = new List<DataChannel>();
                lock (_syncRoot)
                {
                    foreach (var entry in _setups.Values.Where(entry => entry.RoleId == roleId))
                    {
                        foreach (var channel in entry.Channels)
                        {
                            _channels.Remove(channel.Id);
                            retired.Add(channel);
                            RecordActivation(entry, $"DataChannel:channel:{channel.Id}", "Retired because its role was quarantined.", ActivationMode.Disabled);
                        }
                        entry.Materialized = false;
                        entry.Channels = [];
                        SetState(entry, DataChannelSetupState.Disabled, RoleUnavailableException.MessageForRole(roleId));
                    }
                }
                retirement = new Lazy<Task>(() => DrainAndDisposeAsync(retired));
                _roleRetirements.Add(roleId, retirement);
            }
        }
        finally { _lifecycle.Release(); }
        // Deliveries may depend on another setup's lifecycle or on host shutdown to signal cancellation.
        await retirement.Value;
    }

    private static async Task DrainAndDisposeAsync(List<DataChannel> retired)
    {
        var failures = new List<Exception>();
        foreach (var channel in retired.AsEnumerable().Reverse())
        {
            try
            {
                await channel.Pipe.DrainRoleDeliveriesAsync();
                await channel.Pipe.DisposeAsync();
            }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    internal async Task<int> InitializeAutomaticAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = linked.Token;
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            var failures = 0;
            foreach (var entry in _setups.Values.Where(entry => entry.Registration.Mode == ActivationMode.Automatic))
            {
                if (IsQuarantined(entry)) continue;
                try { await StartCoreAsync(entry, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    failures++;
                    _logger.LogError(exception, "Failed to initialize data-channel setup {SetupId}.", entry.Id);
                }
            }
            return failures;
        }
        finally { _lifecycle.Release(); }
    }

    /// <inheritdoc />
    public IReadOnlyList<DataChannelSetupSnapshot> GetSetups()
    {
        lock (_syncRoot)
            return _setups.Values.Select(entry => new DataChannelSetupSnapshot
            {
                Id = entry.Id,
                ImplementationType = entry.Registration.ImplementationType.FullName!,
                Owner = _application.TypeActivation?.GetWorkloadOwner(entry.Registration.ImplementationType, entry.Registration.RegistrationOwner)
                    ?? entry.Registration.RegistrationOwner?.FullName,
                Mode = entry.Registration.Mode,
                State = entry.State,
                ChannelIds = entry.Definitions?.Select(builder => builder.Id).ToArray() ?? [],
                Error = entry.Error
            }).ToArray();
    }

    /// <inheritdoc />
    public Task StartSetupAsync(string setupId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(setupId, StartCoreAsync, cancellationToken);

    /// <inheritdoc />
    public Task StopSetupAsync(string setupId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(setupId, (entry, _) => StopCoreAsync(entry), cancellationToken);

    /// <inheritdoc />
    public Task RestartSetupAsync(string setupId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(setupId, async (entry, token) =>
        {
            await StopCoreAsync(entry);
            await StartCoreAsync(entry, token);
        }, cancellationToken);

    private async Task ExecuteAsync(string setupId, Func<SetupEntry, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupId);
        lock (_syncRoot) ObjectDisposedException.ThrowIf(_stopping, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.ApplicationStopping, _shutdownToken);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        if (!await _lifecycle.WaitAsync(0, token))
            throw new InvalidOperationException("A data-channel lifecycle operation is already in progress in this host.");
        try
        {
            token.ThrowIfCancellationRequested();
            SetupEntry entry;
            lock (_syncRoot)
            {
                if (_stopping) throw new InvalidOperationException("The data-channel host is stopping.");
                if (!_configured) throw new InvalidOperationException("The data-channel host has not completed composition.");
                entry = _setups.GetValueOrDefault(setupId) ?? throw new KeyNotFoundException($"Data-channel setup '{setupId}' was not found.");
                if (IsQuarantined(entry)) throw new RoleUnavailableException(entry.RoleId!);
                if (entry.Registration.Mode == ActivationMode.Disabled)
                    throw new InvalidOperationException($"Data-channel setup '{setupId}' belongs to an owner that is disabled in this host.");
            }
            await action(entry, token);
        }
        catch (ChannelIdentityConflictException duplicate)
        {
            throw new InvalidOperationException(duplicate.Message, duplicate);
        }
        finally { _lifecycle.Release(); }
    }

    private void Declare(SetupEntry entry)
    {
        if (entry.Definitions is not null) return;
        var registrar = new SetupRegistrar();
        try
        {
            var setup = (IDataChannelSetup)_services.GetRequiredService(entry.Registration.ImplementationType);
            setup.Setup(registrar);
        }
        finally { registrar.Close(); }
        lock (_syncRoot)
        {
            foreach (var builder in registrar.Builders)
                if (_channelOwners.ContainsKey(builder.Id))
                    throw new ChannelIdentityConflictException(builder.Id);
            entry.Definitions = registrar.Builders.ToArray();
            foreach (var builder in entry.Definitions) _channelOwners.Add(builder.Id, entry);
        }
    }

    private async Task MaterializeAsync(SetupEntry entry, bool atStartup = false)
    {
        if (entry.Materialized) return;
        var channels = new List<DataChannel>();
        try
        {
            foreach (var builder in entry.Definitions!)
            {
                var pipeline = await builder.BuildAsync(_services);
                if (entry.RoleId is { } roleId) pipeline.BindRole(roleId, _roleHealth!, _shutdownToken);
                var channel = new DataChannel(pipeline) { Reinitializer = token => ReinitializeAsync(entry, pipeline, token) };
                channels.Add(channel);
                if (!atStartup && pipeline.GetComponents().OfType<IApplicationBuilderConfigurable>().Any())
                    throw new InvalidOperationException($"Data-channel setup '{entry.Id}' requires startup-time HTTP configuration and cannot be first materialized at runtime. Use Automatic startup for this setup.");
            }
            lock (_syncRoot)
            {
                entry.Channels = channels.ToArray();
                entry.Materialized = true;
                foreach (var channel in channels)
                {
                    _channels.Add(channel.Id, channel);
                    RecordActivation(entry, $"DataChannel:channel:{channel.Id}", "Materialized by its selected setup; readiness is reported by the channel.");
                }
            }
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            foreach (var channel in channels.AsEnumerable().Reverse())
            {
                try { await channel.Pipe.DisposeAsync(); }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (failures.Count > 1) throw new PipelineRollbackException(failures);
            throw;
        }
    }

    private async Task StartCoreAsync(SetupEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.State == DataChannelSetupState.Running) return;
        SetState(entry, DataChannelSetupState.Starting);
        try
        {
            Declare(entry);
            await MaterializeAsync(entry);
            await Parallel.ForEachAsync(entry.Channels, new ParallelOptions
            {
                MaxDegreeOfParallelism = _initThreadCount,
                CancellationToken = cancellationToken
            }, async (channel, token) => await channel.Pipe.InitAsync(token));
            cancellationToken.ThrowIfCancellationRequested();
            SetState(entry, DataChannelSetupState.Running);
        }
        catch (Exception original)
        {
            var failures = await StopChannelsAsync(entry);
            var failure = failures.Count == 0 ? original : new AggregateException(new[] { original }.Concat(failures));
            SetState(entry, DataChannelSetupState.Failed, failure.Message);
            if (failures.Count > 0) throw failure;
            throw;
        }
    }

    private async Task StopCoreAsync(SetupEntry entry)
    {
        if (entry.State is DataChannelSetupState.Held or DataChannelSetupState.Stopped) return;
        SetState(entry, DataChannelSetupState.Stopping);
        var failures = await StopChannelsAsync(entry);
        if (failures.Count > 0)
        {
            var exception = new AggregateException(failures);
            SetState(entry, DataChannelSetupState.Failed, exception.Message);
            throw exception;
        }
        SetState(entry, DataChannelSetupState.Stopped);
    }

    private static async Task<List<Exception>> StopChannelsAsync(SetupEntry entry)
    {
        var failures = new List<Exception>();
        foreach (var channel in entry.Channels.Reverse())
        {
            try { await channel.Pipe.StopAsync(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        return failures;
    }

    private Task ReinitializeAsync(SetupEntry entry, ChannelPipeline pipeline, CancellationToken cancellationToken) =>
        ExecuteAsync(entry.Id, async (_, token) =>
        {
            if (entry.State is not (DataChannelSetupState.Running or DataChannelSetupState.Failed))
                throw new InvalidOperationException($"Data-channel setup '{entry.Id}' is stopped. Start the setup before reinitializing a channel.");
            SetState(entry, DataChannelSetupState.Starting);
            try
            {
                await pipeline.InitAsync(token);
                SetState(entry, entry.Channels.All(channel => channel.Pipe.IsInitialized)
                    ? DataChannelSetupState.Running : DataChannelSetupState.Failed);
            }
            catch (Exception exception)
            {
                SetState(entry, DataChannelSetupState.Failed, exception.Message);
                throw;
            }
        }, cancellationToken);

    private void SetState(SetupEntry entry, DataChannelSetupState state, string? error = null)
    {
        lock (_syncRoot)
        {
            entry.State = state;
            entry.Error = error;
            RecordActivation(entry, $"DataChannel:setup:{entry.Registration.ImplementationType.FullName}",
                $"Setup lifecycle: {state}. Inspect DataChannel for channel readiness and operation errors.",
                state == DataChannelSetupState.Disabled ? ActivationMode.Disabled : entry.Registration.Mode);
        }
    }

    private void RecordActivation(SetupEntry entry, string key, string reason, ActivationMode? mode = null) => _application.RecordActivation(new ResourceActivation
    {
        Key = key,
        ImplementationType = entry.Registration.ImplementationType,
        RegistrationOwner = entry.Registration.RegistrationOwner,
        Kind = TypeActivationKind.DataChannel,
        Mode = mode ?? entry.Registration.Mode,
        Reason = reason
    });

    /// <inheritdoc />
    public DataChannel? Fetch(string id) { lock (_syncRoot) return _channels.GetValueOrDefault(id); }
    /// <inheritdoc />
    public IReadOnlyList<DataChannel> FetchGroup(string groupId) => FetchAll().Where(channel => channel.Pipe.GroupId == groupId).ToArray();
    /// <inheritdoc />
    public IReadOnlyList<DataChannel> FetchAll() { lock (_syncRoot) return _channels.Values.ToArray(); }

    internal Task ShutdownAsync()
    {
        lock (_syncRoot)
        {
            _stopping = true;
            return _shutdown ??= ShutdownCoreAsync();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        // Cancellation callbacks and endpoint cleanup must run outside the snapshot lock.
        await Task.Yield();
        var failures = new List<Exception>();
        try { await _shutdownCancellation.CancelAsync(); }
        catch (Exception exception) { failures.Add(exception); }
        await _lifecycle.WaitAsync();
        try
        {
            foreach (var entry in _setups.Values.Where(entry => entry.Materialized))
            {
                SetState(entry, DataChannelSetupState.Stopping);
                var entryFailures = new List<Exception>();
                foreach (var channel in entry.Channels.Reverse())
                {
                    try { await channel.Pipe.DisposeAsync(); }
                    catch (Exception exception) { entryFailures.Add(exception); }
                }
                failures.AddRange(entryFailures);
                SetState(entry, entryFailures.Count == 0 ? DataChannelSetupState.Stopped : DataChannelSetupState.Failed,
                    entryFailures.Count == 0 ? null : new AggregateException(entryFailures).Message);
            }
            foreach (var retirement in _roleRetirements.Values)
            {
                try { await retirement.Value; }
                catch (Exception failure) { failures.Add(failure); }
            }
        }
        finally { _lifecycle.Release(); _shutdownCancellation.Dispose(); }
        if (failures.Count > 0) throw new AggregateException(failures).Flatten();
    }

    private sealed class ChannelIdentityConflictException(string id) : InvalidOperationException($"Data channel '{id}' is already registered in the current host.");

    private sealed class SetupEntry(DataChannelSetupRegistration registration, string? roleId)
    {
        public string Id { get; } = $"{registration.ImplementationType.Assembly.GetName().Name}:{registration.ImplementationType.FullName}";
        public DataChannelSetupRegistration Registration { get; } = registration;
        public string? RoleId { get; } = roleId;
        public DataChannelSetupState State { get; set; } = registration.Mode == ActivationMode.Disabled
            ? DataChannelSetupState.Disabled : registration.Mode == ActivationMode.Manual ? DataChannelSetupState.Held : DataChannelSetupState.Stopped;
        public string? Error { get; set; }
        public ChannelPipelineBuilder[]? Definitions { get; set; }
        public DataChannel[] Channels { get; set; } = [];
        public bool Materialized { get; set; }
    }

    private sealed class SetupRegistrar : IDataChannelRegistrar
    {
        private readonly Dictionary<string, ChannelPipelineBuilder> _builders = new(StringComparer.Ordinal);
        private bool _closed;
        public IEnumerable<ChannelPipelineBuilder> Builders => _builders.Values;
        public void Close() => _closed = true;
        public void Add(string id, Action<ChannelPipelineBuilder> configure, string? groupId = null)
        {
            if (_closed) throw new InvalidOperationException("The data-channel setup callback has completed; registration is closed.");
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(configure);
            var builder = new ChannelPipelineBuilder();
            configure(builder);
            builder.Prepare(id, groupId);
            if (!_builders.TryAdd(id, builder))
                throw new ChannelIdentityConflictException(id);
        }
    }
}
