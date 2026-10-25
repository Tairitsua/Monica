using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Monica.Core.Results;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using MudBlazor;

namespace Monica.JobScheduler.UI.Components;

/// <summary>Hosts the complete prospective operator policy editor for one exact definition.</summary>
/// <remarks>
/// The first parameters become the optimistic baseline. A save freezes its exact job identity and complete
/// replacement change before authorization. A conflicting save independently authorizes its reload and rebases
/// untouched fields while preserving drafts. Retirement cancels and drains every admitted backend operation;
/// repeated disposal observes the same completion before the draft is released.
/// </remarks>
public partial class JobPolicyDialog : IAsyncDisposable
{
    private readonly SchedulerOperationLifetime _lifetime = new();
    private JobPolicyEditorState? _state;
    private bool _isSaving;
    private string? _error;

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;
    [Inject] private JobSchedulerFacade Facade { get; set; } = null!;
    [Inject] private IJobSchedulerUiAccess Access { get; set; } = null!;
    [Inject] private IStringLocalizer<JobSchedulerResource> L { get; set; } = null!;
    [Inject] private TimeProvider TimeProvider { get; set; } = null!;
    [Inject] private SchedulerTimePresentation TimePresentation { get; set; } = null!;

    /// <summary>Gets or sets the exact definition used as the initial immutable optimistic policy baseline.</summary>
    /// <remarks>Changing parameters does not retarget an open editor; open a new native dialog for another job.</remarks>
    [Parameter, EditorRequired] public JobDefinition Definition { get; set; } = null!;

    /// <summary>Gets or sets the optional accepted operational projection used to compare the stored schedule cursor.</summary>
    [Parameter] public JobOperationalSummary? OperationalSummary { get; set; }

    /// <summary>Gets or sets when the caller accepted the projection; omission uses the injected clock on initialization.</summary>
    [Parameter] public DateTimeOffset? ObservedAtUtc { get; set; }

    private bool IsDisposed => _lifetime.IsRetired;

    private IReadOnlyList<IdentityFact> IdentityFacts => _state is null ? [] :
    [
        new(L["JobDetail:Contract:SchedulerScope"], _state.Definition.SchedulerScopeKey),
        new(L["Catalog:Columns:Owner"], _state.Definition.OwnerKey),
        new(L["Policy:Identity:JobType"], L[$"JobTypes:{_state.Definition.Declaration.JobType}"]),
        new(L["Policy:Identity:ArgumentContract"], _state.Definition.Declaration.JobArgsKey ?? L["Common:NotAvailable"]),
        new(L["Policy:Identity:PolicyRevision"], _state.Definition.Policy.ConcurrencyStamp),
        new(L["Policy:Identity:UpdatedAt"], TimePresentation.FormatTimestamp(_state.Definition.Policy.UpdatedAtUtc)),
        new(L["Policy:Identity:LastObserved"], TimePresentation.FormatTimestamp(_state.Definition.LastObservedAtUtc))
    ];

    protected override void OnParametersSet() => _lifetime.TryApply(() =>
        _state ??= JobPolicyEditorState.Create(Definition, OperationalSummary, ObservedAtUtc ?? TimeProvider.GetUtcNow()));

    private async Task SaveAsync()
    {
        PolicySaveRequest? request = null;
        _lifetime.TryApply(() =>
        {
            if (_isSaving || _state is not { CanSave: true } state) return;
            request = new(state, state.Definition.Id, state.CreateChange());
            _isSaving = true;
            _error = null;
        });
        if (request is null) return;

        try { await _lifetime.RunAsync(token => SaveOwnedAsync(request, token)); }
        catch (OperationCanceledException) when (IsDisposed) { }
        finally { _lifetime.TryApply(() => _isSaving = false); }
    }

    private async Task SaveOwnedAsync(PolicySaveRequest request, CancellationToken cancellationToken)
    {
        try { await SaveCoreAsync(request, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            ApplyError(request, string.IsNullOrWhiteSpace(exception.Message) ? L["Catalog:Messages:UpdateFailed"] : exception.Message);
        }
    }

    private async Task SaveCoreAsync(PolicySaveRequest request, CancellationToken cancellationToken)
    {
        if (!await Access.IsAuthorizedAsync(cancellationToken))
        {
            ApplyError(request, L["Access:DeniedDescription"]);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Facade.UpdatePolicyAsync(request.JobId.OwnerKey, request.JobId.JobKey,
            request.Change, cancellationToken);
        if (IsDisposed || !ReferenceEquals(_state, request.State)) return;

        if (result.IsFailed(out var error, out var policy))
        {
            if (result.Status == ResStatus.Conflict) await RebaseAfterConflictAsync(request, cancellationToken);
            else ApplyError(request, error.Message);
            return;
        }
        if (!_lifetime.TryApply(() => request.State.ClearConflict())) return;
        if (!IsDisposed) MudDialog.Close(DialogResult.Ok(policy));
    }

    private async Task RebaseAfterConflictAsync(PolicySaveRequest request, CancellationToken cancellationToken)
    {
        // The facade does not authorize reads; a conflict reload is a separate access decision.
        if (!await Access.IsAuthorizedAsync(cancellationToken))
        {
            ApplyError(request, L["Access:DeniedDescription"]);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var latestResult = await Facade.GetOperationalSummaryAsync(request.JobId, cancellationToken);
        if (IsDisposed || !ReferenceEquals(_state, request.State)) return;
        if (latestResult.IsFailed(out var loadError, out var latest) || latest is null)
        {
            ApplyError(request, loadError?.Message ?? L["Policy:Conflict:ReloadFailed"]);
            return;
        }
        var observedAt = TimeProvider.GetUtcNow();
        _lifetime.TryApply(() =>
        {
            request.State.RebaseAfterConflict(latest.Definition, latest, observedAt);
            _error = null;
        });
    }

    private void ApplyError(PolicySaveRequest request, string? error) => _lifetime.TryApply(() =>
    {
        if (ReferenceEquals(_state, request.State)) _error = error;
    });

    private void UpdateDraft(Action<JobPolicyEditorState> update) => _lifetime.TryApply(() =>
    {
        if (_isSaving || _state is not { } state) return;
        update(state);
        _error = null;
    });

    private void SetDisabledOverride(bool? value) => UpdateDraft(state => state.DisabledOverride = value);
    private void SetDisplayName(string? value) => UpdateDraft(state => state.SetDisplayName(value));
    private void ResetDisplayName() => UpdateDraft(static state => state.ResetDisplayName());
    private void SetDescriptionMode(JobDescriptionOverrideMode value) => UpdateDraft(state => state.DescriptionMode = value);
    private void SetDescription(string? value) => UpdateDraft(state => state.DescriptionDraft = value ?? string.Empty);
    private void SetMaxConcurrency(int? value) => UpdateDraft(state => state.MaxConcurrencyOverride = value);
    private void SetRetryCount(int? value) => UpdateDraft(state => state.RetryCountOverride = value);
    private void SetMaxRecords(int? value) => UpdateDraft(state => state.MaxRetainedHistoryRecords = value);
    private void SetMaxRetentionDays(int? value) => UpdateDraft(state => state.MaxRetentionDays = value);
    private void ResetMaxRetentionDays() => UpdateDraft(static state => state.MaxRetentionDays = null);
    private void EnableTimeoutOverride() => UpdateDraft(static state => state.EnableTimeoutOverride());
    private void ResetTimeout() => UpdateDraft(static state => state.ResetTimeout());
    private void SetTimeoutHours(int value) => UpdateDraft(state => state.TimeoutHours = value);
    private void SetTimeoutMinutes(int value) => UpdateDraft(state => state.TimeoutMinutes = value);
    private void SetTimeoutSeconds(int value) => UpdateDraft(state => state.TimeoutSeconds = value);
    private void ResetAll() => UpdateDraft(static state => state.ResetAll());
    private void DraftChanged() => UpdateDraft(static _ => { });
    private void DiscardScheduleDraft() => UpdateDraft(static state => state.DiscardScheduleDraft());

    private string EnabledLabel(bool enabled) => enabled ? L["Policy:Values:Enabled"] : L["Policy:Values:Disabled"];
    private string DisplayText(string? value) => string.IsNullOrWhiteSpace(value) ? L["Common:NotAvailable"] : value;
    private static string FormatDuration(TimeSpan value) => value.Ticks % TimeSpan.TicksPerSecond == 0
        ? value.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture)
        : value.ToString(@"d\.hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);
    private void Cancel() { if (!IsDisposed && !_isSaving) MudDialog.Cancel(); }

    /// <summary>Cancels and drains the admitted save, then releases its draft; concurrent calls share actual completion.</summary>
    public ValueTask DisposeAsync() => _lifetime.RetireAsync(() => _state = null);

    private sealed record IdentityFact(string Label, string Value);
    private sealed record PolicySaveRequest(JobPolicyEditorState State, JobId JobId, JobPolicyChange Change);
}
