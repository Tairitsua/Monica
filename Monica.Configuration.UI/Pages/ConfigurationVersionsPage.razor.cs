using Monica.Configuration.Models;
using Monica.Configuration.UI.Dialogs;
using Monica.Configuration.UI.State;
using Monica.Configuration.UI.Support;
using MudBlazor;

namespace Monica.Configuration.UI.Pages;

/// <summary>Reviews retained automatic snapshots and explicit saved-snapshot comparisons.</summary>
/// <remarks>Each page owns its state and native dialog sessions. Disposal joins actual tokenless producers
/// and rejects late UI adoption; it cannot cancel or reverse a persisted rollback or deletion.</remarks>
public partial class ConfigurationVersionsPage
{
    /// <summary>Gets the Configuration Versions route.</summary>
    public const string PAGE_URL = ConfigurationUiRoutes.VERSIONS_ROUTE;
    private readonly ConfigurationOperationLifetime _operations = new();
    private ConfigurationVersionsPageState State = default!;
    private long? _originVersion;
    private long? _targetVersion;
    private int _selectedTab;
    private bool _dialogOpen;

    protected override async Task OnInitializedAsync()
    {
        State = new ConfigurationVersionsPageState(Facade, Snackbar, L);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_operations.IsRetired) return;
        try
        {
            await State.LoadAsync();
            _operations.TryApply(SynchronizePair);
        }
        catch (OperationCanceledException) when (_operations.IsRetired || State.IsDisposed) { }
    }

    private void SynchronizePair()
    {
        _originVersion = State.OriginVersion;
        _targetVersion = State.TargetVersion;
    }

    private string SnapshotOption(ConfigurationUnifiedVersionSummary snapshot) =>
        $"{ConfigurationUnifiedVersionDisplayFormatter.VersionLabel(snapshot)} · {TimeDisplay.Format(snapshot.CreatedTime)}";

    private async Task SelectSnapshotAsync(long version)
    {
        if (_operations.IsRetired) return;
        _selectedTab = 1;
        try { await State.SelectVersionAsync(version); _operations.TryApply(SynchronizePair); }
        catch (OperationCanceledException) when (_operations.IsRetired || State.IsDisposed) { }
    }

    private async Task ComparePreviousAsync(ConfigurationUnifiedVersionSummary snapshot)
    {
        if (_operations.IsRetired) return;
        _selectedTab = 1;
        try { await State.CompareWithPreviousAsync(snapshot); _operations.TryApply(SynchronizePair); }
        catch (OperationCanceledException) when (_operations.IsRetired || State.IsDisposed) { }
    }

    private async Task CompareAsync()
    {
        if (_operations.IsRetired || _originVersion is not { } origin || _targetVersion is not { } target) return;
        try { await State.SelectComparisonAsync(origin, target); }
        catch (OperationCanceledException) when (_operations.IsRetired || State.IsDisposed) { }
    }

    private async Task RollbackAsync(ConfigurationUnifiedVersionSummary snapshot)
    {
        if (_operations.IsRetired || _dialogOpen) return;
        var version = snapshot.Version;
        _dialogOpen = true;
        try
        {
            await _operations.RunAsync(async token =>
            {
                var parameters = new DialogParameters<ConfigurationUnifiedVersionRollbackDialog>();
                parameters.Add(dialog => dialog.Version, version);
                var result = await _operations.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationUnifiedVersionRollbackDialog>(
                    L["Versions:Rollback:Title", ConfigurationUnifiedVersionJsonFormatter.VersionLabel(version)], parameters,
                    new DialogOptions { MaxWidth = MaxWidth.ExtraLarge, FullWidth = true, CloseButton = true, CloseOnEscapeKey = true }));
                if (_operations.IsRetired || result is not { Canceled: false, Data: ConfigurationUnifiedVersionRollbackResult rollback }) return;
                if (rollback.ApplyResult.Status == ConfigurationMutationGroupApplyStatus.PartiallyApplied)
                    Snackbar.Add(L["Versions:Rollback:Messages:PartiallyApplied", rollback.MutationGroup.GroupId], Severity.Error);
                else if (rollback.ApplyResult.PostCommitIssues.Count > 0)
                    Snackbar.Add(L["Versions:Rollback:Messages:AppliedWithIssues", rollback.MutationGroup.GroupId], Severity.Warning);
                else Snackbar.Add(L["Rollback:Success"], Severity.Success);

                if (rollback.ApplyResult.PostCommitIssues.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var issues = new DialogParameters<ConfigurationPostCommitIssuesDialog>();
                    issues.Add(dialog => dialog.Issues, rollback.ApplyResult.PostCommitIssues);
                    issues.Add(dialog => dialog.SummaryText, L["Versions:Rollback:Messages:AppliedWithIssues", rollback.MutationGroup.GroupId].Value);
                    await _operations.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationPostCommitIssuesDialog>(
                        L["Dialogs:PostCommitIssues:Title"], issues, ConfigurationPostCommitIssuesDialog.DefaultOptions));
                }
                if (!_operations.IsRetired) await ReloadAsync();
            });
        }
        catch (OperationCanceledException) when (_operations.IsRetired) { }
        finally { _operations.TryApply(() => _dialogOpen = false); }
    }

    /// <summary>Closes owned dialogs and drains actual page and state work without disposing borrowed services.</summary>
    public async ValueTask DisposeAsync()
    {
        // Retire both owners before awaiting either, so an admitted callback cannot re-enter the other owner.
        var page = _operations.RetireAsync().AsTask();
        var state = State is null ? Task.CompletedTask : State.DisposeAsync().AsTask();
        await Task.WhenAll(page, state);
    }
}
