using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Configuration.UI.Support;
using Monica.Core.Results;

namespace Monica.Configuration.UI.State;

/// <summary>Tracks an exact source-key file read without adopting an older source's late response.</summary>
internal sealed class ConfigurationSourceFileState : IAsyncDisposable
{
    private readonly ConfigurationOperationLifetime _lifetime;
    private readonly Func<string, Task<Result<ConfigurationSourceFileView>>> _load;

    internal ConfigurationSourceFileState(ConfigurationFacade facade, string unavailableText,
        ConfigurationOperationLifetime lifetime, bool showSensitiveValues = false)
        : this(key => facade.GetSourceFileViewAsync(key, showSensitiveValues), unavailableText, lifetime) { }

    internal ConfigurationSourceFileState(Func<string, Task<Result<ConfigurationSourceFileView>>> load,
        string unavailableText, ConfigurationOperationLifetime? lifetime = null)
    {
        _load = load;
        _lifetime = lifetime ?? new();
        Read = new(_lifetime, unavailableText);
    }

    internal string? SourceKey { get; private set; }
    internal ConfigurationReadState<ConfigurationSourceFileView> Read { get; }

    /// <summary>Retains same-key refresh evidence, but immediately clears evidence belonging to a different key.</summary>
    /// <remarks>Keys use ordinal identity without trimming. Disposal drains the actual tokenless file read.</remarks>
    internal Task LoadAsync(string sourceKey) => _lifetime.RunAsync(async _ =>
    {
        long request = 0;
        if (!_lifetime.TryApply(() =>
            {
                var changed = !string.Equals(SourceKey, sourceKey, StringComparison.Ordinal);
                SourceKey = sourceKey;
                request = Read.BeginRead(discardAccepted: changed);
            })) return;
        await Read.ReadReservedAsync(request, () => _load(sourceKey));
    });

    public ValueTask DisposeAsync() => _lifetime.RetireAsync();
}
