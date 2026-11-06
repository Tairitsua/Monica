using Monica.Configuration.UI.State;
using Monica.Configuration.UI.Support;
using Monica.Core.Configuration;
using Monica.Core.Results;

namespace Monica.Configuration.UI.Pages;

/// <summary>Displays the current raw Microsoft configuration debug view with deliberate credential reveal.</summary>
/// <remarks>
/// Credential-like masking is heuristic, not complete schema redaction. Each page owns its read lifetime;
/// custom owners must await DisposeAsync. Retirement drains actual tokenless reads without disposing the facade.
/// </remarks>
public partial class ConfigurationDebugPage
{
    /// <summary>Gets the Configuration Diagnostics route.</summary>
    public const string PAGE_URL = ConfigurationUiRoutes.DEBUG_ROUTE;
    private readonly ConfigurationOperationLifetime _operations = new();
    private static readonly IReadOnlySet<int> EmptyRevealedLines = new HashSet<int>();
    private ConfigurationDebugPageState? _state;
    private string _searchText = string.Empty;
    private ApplicationDebugModeState? _debugModeState;
    private string? _debugModeError;

    private IReadOnlyList<ConfigurationDebugLine> _lines => _state?.Read.Value ?? [];
    private IReadOnlySet<int> _revealedLines => _state?.RevealedLines ?? EmptyRevealedLines;
    private bool _loading => _state?.Read.Loading ?? true;
    private bool _hasLoadedDebugView => _state?.Read.HasLoaded ?? false;
    private string? _loadError => _state?.Read.Error;
    private DateTimeOffset? _debugObservedAtUtc => _state?.Read.ObservedAtUtc;
    private IEnumerable<ConfigurationDebugLine> FilteredLines => string.IsNullOrWhiteSpace(_searchText)
        ? _lines : _lines.Where(IsMatch);
    private int SensitiveLineCount => _lines.Count(static line => line.IsSensitive);

    protected override async Task OnInitializedAsync()
    {
        _state = new(Facade, L["Common:Errors:LoadFailed"], _operations);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_operations.IsRetired || _state is null) return;
        try
        {
            var policy = Facade.GetDebugModeState();
            var failure = policy;
            _debugModeState = policy.Data;
            _debugModeError = policy.IsFailure() || _debugModeState is null
                ? failure.Message ?? L["Common:Errors:LoadFailed"] : null;
            await _state.LoadAsync();
        }
        catch (OperationCanceledException) when (_operations.IsRetired) { }
    }

    private string FormatDebugMode(bool? enabled) => enabled switch
    {
        true => L["Debug:Policy:Enabled"],
        false => L["Debug:Policy:Disabled"],
        null => L["Debug:Policy:Unavailable"]
    };

    private string FormatPolicySource(string? source) => source switch
    {
        "Code" => L["Debug:Policy:SourceCode"],
        "Default" => L["Debug:Policy:SourceDefault"],
        null => L["Debug:Policy:Unavailable"],
        _ => source
    };

    private bool IsMatch(ConfigurationDebugLine line) => string.IsNullOrWhiteSpace(_searchText)
        || (Visibility.ShowSensitiveValues ? line.RawText : line.SearchText).Contains(_searchText, StringComparison.OrdinalIgnoreCase);

    private void ToggleSensitiveLine(int lineIndex) => _state?.ToggleSensitiveLine(lineIndex);

    /// <summary>Rejects new work and joins actual read completion; repeated calls share the same teardown.</summary>
    public ValueTask DisposeAsync() => _operations.RetireAsync();
}
