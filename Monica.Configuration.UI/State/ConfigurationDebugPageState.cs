using Monica.Configuration.Facades;
using Monica.Configuration.UI.Support;
using Monica.Core.Results;

namespace Monica.Configuration.UI.State;

/// <summary>Owns accepted debug lines with credential-heuristic masking, not complete schema-based privacy.</summary>
/// <remarks>Raw debug text is deliberately available for per-line reveal. Search uses each line's safe SearchText.</remarks>
internal sealed class ConfigurationDebugPageState : IAsyncDisposable
{
    private readonly ConfigurationOperationLifetime _lifetime;
    private readonly Func<Task<Result<string>>> _load;
    private readonly string _unavailableText;
    private readonly HashSet<int> _revealedLines = [];

    internal ConfigurationDebugPageState(ConfigurationFacade facade, string unavailableText,
        ConfigurationOperationLifetime lifetime) : this(facade.GetDebugViewAsync, unavailableText, lifetime) { }

    internal ConfigurationDebugPageState(Func<Task<Result<string>>> load, string unavailableText,
        ConfigurationOperationLifetime? lifetime = null)
    {
        _load = load;
        _unavailableText = unavailableText;
        _lifetime = lifetime ?? new();
        Read = new(_lifetime, unavailableText);
    }

    internal ConfigurationReadState<IReadOnlyList<ConfigurationDebugLine>> Read { get; }
    internal IReadOnlySet<int> RevealedLines => _revealedLines;

    internal Task LoadAsync() => _lifetime.RunAsync(async _ =>
    {
        long request = 0;
        if (!_lifetime.TryApply(() =>
            {
                _revealedLines.Clear();
                request = Read.BeginRead();
            })) return;
        await Read.ReadReservedAsync(request, async () =>
        {
            var result = await _load();
            var text = result.Data;
            if (result.IsFailure()) return Result.FromFailure<IReadOnlyList<ConfigurationDebugLine>>(result);
            return text is null ? Result.Failure<IReadOnlyList<ConfigurationDebugLine>>(_unavailableText)
                : Result.Success<IReadOnlyList<ConfigurationDebugLine>>(Array.AsReadOnly(ConfigurationDebugValueRedactor.Parse(text).ToArray()));
        });
    });

    /// <summary>Changes deliberate reveal only for an accepted sensitive line while no refresh is pending.</summary>
    internal void ToggleSensitiveLine(int lineIndex) => _lifetime.TryApply(() =>
    {
        if (Read.Loading || Read.Value?.Any(line => line.Index == lineIndex && line.IsSensitive) != true) return;
        if (!_revealedLines.Add(lineIndex)) _revealedLines.Remove(lineIndex);
    });

    public ValueTask DisposeAsync() => _lifetime.RetireAsync();
}
