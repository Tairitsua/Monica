using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Models;
using Monica.DataChannel.Pipeline;
using Monica.Modules;

namespace Monica.DataChannel.Services;

/// <summary>Owns setup admission, channel identity and endpoint lifetimes for one host.</summary>
internal sealed class DataChannelRuntime : IDataChannelRegistrar, IDataChannelManager, IDataChannelLifecycle
{
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly IServiceProvider _services;
    private readonly MonicaApplication _application;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DataChannelRuntime> _logger;
    private readonly int _initThreadCount;
    private readonly Dictionary<string, SetupEntry> _setups;
    private readonly Dictionary<string, SetupEntry> _channelOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DataChannel> _channels = new(StringComparer.Ordinal);
    private bool _configured;
    private bool _stopping;
    private Task? _shutdown;
    private readonly CancellationTokenSource _shutdownCancellation = new();
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
        _shutdownToken = _shutdownCancellation.Token;
        _setups = registrations.Select(registration => new SetupEntry(registration)).ToDictionary(entry => entry.Id, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public void Add(string id, Action<ChannelPipelineBuilder> configure, string? groupId = null) =>
        throw new InvalidOperationException("Data-channel registration is confined to the selected setup callback.");

    internal void ConfigureApplication(IApplicationBuilder app)
    {
        if (_configured) throw new InvalidOperationException("Data-channel pipelines have already been configured for this host.");
        var automatic = _setups.Values.Where(entry => entry.Registration.Mode == ActivationMode.Automatic).ToArray();
        // Detect duplicate identities across every automatic declaration before building any endpoint.
        foreach (var entry in automatic) Declare(entry);
        foreach (var entry in automatic) MaterializeAsync(entry, app).GetAwaiter().GetResult();
        _configured = true;
    }

    internal void ConfigureEndpoints(IApplicationBuilder app)
    {
        foreach (var component in FetchAll().SelectMany(channel => channel.Pipe.GetComponents()).OfType<IApplicationBuilderConfigurable>())
            component.ConfigEndpoints(app);
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
                if (entry.Registration.Mode == ActivationMode.Disabled)
                    throw new InvalidOperationException($"Data-channel setup '{setupId}' belongs to an owner that is disabled in this host.");
            }
            await action(entry, token);
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
                    throw new InvalidOperationException($"Data channel '{builder.Id}' is already registered in the current host.");
            entry.Definitions = registrar.Builders.ToArray();
            foreach (var builder in entry.Definitions) _channelOwners.Add(builder.Id, entry);
        }
    }

    private async Task MaterializeAsync(SetupEntry entry, IApplicationBuilder? startupApp = null)
    {
        if (entry.Materialized) return;
        var channels = new List<DataChannel>();
        try
        {
            foreach (var builder in entry.Definitions!)
            {
                var pipeline = builder.Build(_services);
                var channel = new DataChannel(pipeline) { Reinitializer = token => ReinitializeAsync(entry, pipeline, token) };
                channels.Add(channel);
                var components = pipeline.GetComponents().OfType<IApplicationBuilderConfigurable>().ToArray();
                if (startupApp is null && components.Length != 0)
                    throw new InvalidOperationException($"Data-channel setup '{entry.Id}' requires startup-time HTTP configuration and cannot be first materialized at runtime. Use Automatic startup for this setup.");
                foreach (var component in components) component.ConfigApplicationBuilder(startupApp!);
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
            if (failures.Count > 1) throw new AggregateException(failures);
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
            RecordActivation(entry, $"DataChannel:setup:{entry.Registration.ImplementationType.FullName}", $"Setup lifecycle: {state}. Inspect DataChannel for channel readiness and operation errors.");
        }
    }

    private void RecordActivation(SetupEntry entry, string key, string reason) => _application.RecordActivation(new ResourceActivation
    {
        Key = key,
        ImplementationType = entry.Registration.ImplementationType,
        RegistrationOwner = entry.Registration.RegistrationOwner,
        Kind = TypeActivationKind.DataChannel,
        Mode = entry.Registration.Mode,
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
        }
        finally { _lifecycle.Release(); _shutdownCancellation.Dispose(); }
        if (failures.Count > 0) throw new AggregateException(failures).Flatten();
    }

    private sealed class SetupEntry(DataChannelSetupRegistration registration)
    {
        public string Id { get; } = $"{registration.ImplementationType.Assembly.GetName().Name}:{registration.ImplementationType.FullName}";
        public DataChannelSetupRegistration Registration { get; } = registration;
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
                throw new InvalidOperationException($"Data channel '{id}' is already registered in the current host.");
        }
    }
}
