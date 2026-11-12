using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace Monica.Core.Configuration;

/// <summary>Reports the last accepted environment-file revision and any rejected reload without exposing values.</summary>
public sealed record EnvironmentFileConfigurationDiagnostic
{
    /// <summary>Gets the resolved source file path.</summary>
    public required string Path { get; init; }
    /// <summary>Gets the count of accepted loads.</summary>
    public long AcceptedRevision { get; init; }
    /// <summary>Gets a path-only rejection diagnostic, or null after a successful load.</summary>
    public string? Error { get; init; }
    /// <summary>Gets the time of the last rejected candidate, if any.</summary>
    public DateTimeOffset? LastRejectedAt { get; init; }
}

/// <summary>Configures the insertion boundary for a host with a custom configuration-source layout.</summary>
public static class MonicaEnvironmentFileBuilderExtensions
{
    internal static readonly object INSERTION_INDEX_KEY = new();

    /// <summary>Inserts selected environment files before the source at <paramref name="sourceIndex"/>.</summary>
    /// <remarks>
    /// Call before AddMonica, after ordinary sources have been supplied. The default host needs no override: its
    /// boundary follows appsettings.{Environment}.json and precedes secrets, environment variables and command line.
    /// A custom host without that identifiable boundary must supply this index. The current source count appends.
    /// </remarks>
    public static TBuilder SetMonicaEnvironmentFileInsertionIndex<TBuilder>(this TBuilder builder, int sourceIndex)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (sourceIndex < 0 || sourceIndex > builder.Configuration.Sources.Count)
            throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        builder.Properties[INSERTION_INDEX_KEY] = sourceIndex;
        return builder;
    }
}

internal sealed class EnvironmentFileConfiguration
{
    private const string SECTION_PATH = "Application:EnvironmentFiles";
    private readonly List<EnvironmentFileState> _files = [];

    internal IReadOnlyList<EnvironmentFileConfigurationDiagnostic> Diagnostics
        => _files.Select(file => file.GetDiagnostic()).ToArray();

    internal void Load(IHostApplicationBuilder builder, Action markMutation, Action<string> reportError)
    {
        if (builder.Configuration is not IConfigurationRoot configurationRoot)
            throw new InvalidOperationException("Monica startup inputs require a host configuration manager exposing IConfigurationRoot provider provenance.");
        var selected = ConfigurationSnapshot.Capture(configurationRoot).SelectCompleteSection(SECTION_PATH);
        if (selected is null) return;
        var section = selected.GetSection(SECTION_PATH);
        if (!selected.TryGetShape(SECTION_PATH, out var shape)
            || shape is not (ConfigurationValueShape.List or ConfigurationValueShape.Container))
            throw new InvalidOperationException($"'{SECTION_PATH}' must be a complete list of file paths (provider {selected.Origin.ProviderPath}).");
        var children = section.GetChildren().ToArray();
        var paths = new List<string>();
        for (var index = 0; index < children.Length; index++)
        {
            var child = children[index];
            if (child.Key != index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || string.IsNullOrWhiteSpace(child.Value) || child.GetChildren().Any())
                throw new InvalidOperationException($"'{SECTION_PATH}' must contain contiguous, nonempty file paths (provider {selected.Origin.ProviderPath}).");
            paths.Add(System.IO.Path.GetFullPath(child.Value, builder.Environment.ContentRootPath));
        }
        if (paths.Count == 0) return;
        var insertion = GetInsertionIndex(builder);
        var originalInsertion = insertion;
        var contributions = configurationRoot.Providers.Select(ConfigurationSnapshot.CaptureProjection).ToArray();
        foreach (var path in paths)
        {
            var state = new EnvironmentFileState(path, reportError);
            var source = new EnvironmentFileConfigurationSource(state)
            {
                Path = path, Optional = false, ReloadOnChange = true
            };
            source.ResolveFileProvider();
            _files.Add(state);
            markMutation();
            builder.Configuration.Sources.Insert(insertion++, source);
        }
        // ConfigurationManager rebuilds providers after source insertion. Keep values assigned to already loaded
        // providers before AddMonica, rather than silently reverting them to their source's initial contents.
        var rebuilt = configurationRoot.Providers.ToArray();
        if (rebuilt.Length != contributions.Length + paths.Count)
            throw new InvalidOperationException("Configuration provider topology changed while inserting environment files.");
        for (var index = 0; index < contributions.Length; index++)
        {
            var provider = rebuilt[index < originalInsertion ? index : index + paths.Count];
            var contribution = contributions[index];
            if (provider is ShapeAwareJsonConfigurationProvider json) json.RestoreProjection(contribution);
            else if (provider is ConfigurationValueProjectionProvider projection) projection.RestoreProjection(contribution);
            else foreach (var (key, value) in contribution.Values)
                if (!provider.TryGet(key, out var loaded) || !string.Equals(loaded, value, StringComparison.Ordinal))
                    provider.Set(key, value);
        }
    }

    internal void Validate(Action<ConfigurationProviderSnapshot> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        foreach (var file in _files) file.Validate(validate);
    }

    private static int GetInsertionIndex(IHostApplicationBuilder builder)
    {
        if (builder.Properties.TryGetValue(MonicaEnvironmentFileBuilderExtensions.INSERTION_INDEX_KEY, out var value)
            && value is int explicitIndex)
        {
            if (explicitIndex < 0 || explicitIndex > builder.Configuration.Sources.Count)
                throw new InvalidOperationException("The configured environment-file insertion index is outside the source collection.");
            return explicitIndex;
        }

        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "appsettings.json", $"appsettings.{builder.Environment.EnvironmentName}.json"
        };
        var matches = builder.Configuration.Sources.Select((source, index) => (source, index))
            .Where(item => item.source is JsonConfigurationSource json
                && json.Path is { } path && fileNames.Contains(path))
            .Select(item => item.index).ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException("Environment files require an identifiable appsettings boundary. Configure the custom host with SetMonicaEnvironmentFileInsertionIndex before AddMonica.");
        return matches[^1] + 1;
    }

    internal sealed class EnvironmentFileState(string path, Action<string> reportError)
    {
        private readonly Lock _gate = new();
        private ConfigurationValueProjection? _accepted;
        private ConfigurationProviderSnapshot? _snapshot;
        private Action<ConfigurationProviderSnapshot>? _validate;
        private long _revision;
        private string? _error;
        private DateTimeOffset? _lastRejectedAt;
        private ConfigurationProviderOrigin _origin = new()
        {
            ProviderType = typeof(EnvironmentFileConfigurationProvider), PriorityIndex = 0,
            ProviderPath = $"environment:{path}", SourcePath = path, PhysicalPath = path,
            Optional = false, ReloadOnChange = true
        };

        internal void SetOrigin(ConfigurationProviderOrigin origin)
        {
            lock (_gate)
            {
                _origin = origin;
                if (_accepted is not null) _snapshot = new ConfigurationProviderSnapshot(origin, _accepted);
            }
        }

        internal ConfigurationValueProjection Capture()
        {
            lock (_gate) return _accepted ?? new ConfigurationValueProjection(
                new Dictionary<string, string?>(), new Dictionary<string, ConfigurationValueShape>());
        }

        internal ConfigurationProviderSnapshot CaptureProvider()
        {
            lock (_gate) return _snapshot ??= new ConfigurationProviderSnapshot(_origin, Capture());
        }

        internal void Load(Stream stream)
        {
            ConfigurationValueProjection candidate;
            try
            {
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
                });
                candidate = ConfigurationValueProjectionFactory.CreateDocument(document.RootElement);
            }
            catch (Exception)
            {
                Reject("The JSON document is malformed or contains duplicate configuration paths.");
                return;
            }

            lock (_gate)
            {
                var snapshot = new ConfigurationProviderSnapshot(_origin, candidate);
                try
                {
                    if (snapshot.Contains(SECTION_PATH) || ConfigurationShapeView.HasBarrier(snapshot, SECTION_PATH))
                        throw new ConfigurationSourcePathException(SECTION_PATH);
                    _validate?.Invoke(snapshot);
                }
                catch (Exception exception)
                {
                    Reject(SafeValidationReason(exception));
                    return;
                }
                _accepted = candidate;
                _snapshot = snapshot;
                _revision++;
                _error = null;
            }
        }

        internal void Validate(Action<ConfigurationProviderSnapshot> validate)
        {
            lock (_gate)
            {
                if (_validate is not null && !ReferenceEquals(_validate, validate))
                    throw new InvalidOperationException("Environment-file ownership validation has already been installed.");
                if (_accepted is { } accepted)
                {
                    try { validate(new ConfigurationProviderSnapshot(_origin, accepted)); }
                    catch (Exception exception)
                    {
                        RecordRejection(SafeValidationReason(exception));
                        throw new InvalidDataException(_error);
                    }
                }
                _validate = validate;
            }
        }

        internal bool HandleFileError()
        {
            lock (_gate)
            {
                RecordRejection("The required file could not be read.");
                return _accepted is not null;
            }
        }

        private void Reject(string reason)
        {
            lock (_gate)
            {
                RecordRejection(reason);
                if (_accepted is null) throw new InvalidDataException(_error);
            }
        }

        private void RecordRejection(string reason)
        {
            _error = $"Environment file '{path}' was rejected: {reason}";
            _lastRejectedAt = DateTimeOffset.UtcNow;
            reportError(_error);
        }

        private static string SafeValidationReason(Exception exception) => exception is ConfigurationSourcePathException path
            ? path.Message : "The candidate failed source validation.";

        internal EnvironmentFileConfigurationDiagnostic GetDiagnostic()
        {
            lock (_gate) return new EnvironmentFileConfigurationDiagnostic
            {
                Path = path, AcceptedRevision = _revision, Error = _error, LastRejectedAt = _lastRejectedAt
            };
        }
    }

    internal sealed class EnvironmentFileConfigurationSource(EnvironmentFileState state) : JsonConfigurationSource
    {
        public override IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            EnsureDefaults(builder);
            return new EnvironmentFileConfigurationProvider(this, state);
        }
    }

    private sealed class EnvironmentFileConfigurationProvider : JsonConfigurationProvider,
        IConfigurationValueShapeProvider, IConfigurationValueSnapshotProvider, IDeploymentConfigurationProvider
    {
        private readonly EnvironmentFileState _state;
        private readonly Lock _reloadGate = new();
        private Timer? _reloadTimer;
        private IDisposable? _watch;
        private bool _disposed;

        internal EnvironmentFileConfigurationProvider(FileConfigurationSource source, EnvironmentFileState state)
            : base(CreateUnwatchedSource(source, state))
        {
            _state = state;
            // The inherited watcher treats deleted files as empty and can throw while opening a stream. Own the
            // complete acquisition boundary instead; the private source copy retains truthful diagnostic metadata.
            Source.ReloadOnChange = source.ReloadOnChange;
        }

        private static JsonConfigurationSource CreateUnwatchedSource(FileConfigurationSource source, EnvironmentFileState state)
        {
            var path = source.Path;
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return new EnvironmentFileConfigurationSource(state)
            {
                Path = path, FileProvider = source.FileProvider, Optional = source.Optional,
                ReloadOnChange = false, ReloadDelay = source.ReloadDelay
            };
        }

        internal void SetOrigin(ConfigurationProviderOrigin origin) => _state.SetOrigin(origin);
        public override void Load(Stream stream) => _state.Load(stream);
        public override bool TryGet(string key, out string? value) => _state.Capture().Values.TryGetValue(key, out value);
        public bool TryGetShape(string path, out ConfigurationValueShape shape) => _state.Capture().Shapes.TryGetValue(path, out shape);
        public ConfigurationValueProjection CaptureProjection() => _state.Capture();
        public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
        {
            return _state.CaptureProvider().GetChildKeys(earlierKeys, parentPath);
        }
        public override void Set(string key, string? value)
            => throw new NotSupportedException("Environment files are deployment-owned; edit the file and restart for startup changes.");

        public override void Load()
        {
            lock (_reloadGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (LoadFile(throwOnInitialFailure: true)) OnReload();
                EnsureWatching();
            }
        }

        private void EnsureWatching()
        {
            if (_watch is not null || !Source.ReloadOnChange || Source.FileProvider is null) return;
            _reloadTimer = new Timer(_ => ReloadFromChange(), null, Timeout.Infinite, Timeout.Infinite);
            _watch = ChangeToken.OnChange(() => Source.FileProvider.Watch(Source.Path!), OnFileChanged);
        }

        private bool LoadFile(bool throwOnInitialFailure)
        {
            var revision = _state.GetDiagnostic().AcceptedRevision;
            using var content = new MemoryStream();
            try
            {
                var file = Source.FileProvider?.GetFileInfo(Source.Path ?? string.Empty);
                if (file is null || !file.Exists) throw new FileNotFoundException();
                using var stream = file.CreateReadStream();
                stream.CopyTo(content);
            }
            catch (Exception exception)
            {
                var retained = _state.HandleFileError();
                if (throwOnInitialFailure && !retained)
                {
                    var message = $"The required environment file '{Source.Path}' could not be read.";
                    if (exception is FileNotFoundException) throw new FileNotFoundException(message);
                    throw new InvalidDataException(message);
                }
                return false;
            }

            content.Position = 0;
            try { _state.Load(content); }
            catch (InvalidDataException) when (!throwOnInitialFailure) { return false; }
            return _state.GetDiagnostic().AcceptedRevision != revision;
        }

        private void OnFileChanged()
        {
            lock (_reloadGate)
            {
                if (_disposed) return;
                if (Source.ReloadDelay > 0) _reloadTimer?.Change(Source.ReloadDelay, Timeout.Infinite);
                else ReloadFromChange();
            }
        }

        private void ReloadFromChange()
        {
            lock (_reloadGate)
            {
                if (_disposed) return;
                if (LoadFile(throwOnInitialFailure: false)) OnReload();
            }
        }

        protected override void Dispose(bool disposing)
        {
            lock (_reloadGate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            // Unregister outside the gate: disposal may wait for a callback already waiting to acquire it.
            _watch?.Dispose();
            _reloadTimer?.Dispose();
            base.Dispose(disposing);
        }
    }

    internal void CaptureOrigins(IConfigurationRoot configuration)
    {
        foreach (var (provider, index) in configuration.Providers.Select((provider, index) => (provider, index)))
            if (provider is EnvironmentFileConfigurationProvider environment)
            {
                var origin = ConfigurationSnapshot.GetOrigin(provider, index, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                environment.SetOrigin(origin);
            }
    }
}
