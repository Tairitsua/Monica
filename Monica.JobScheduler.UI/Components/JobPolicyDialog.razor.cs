using System.Globalization;
using System.Numerics;
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

/// <summary>Hosts a focused schedule or execution/retention policy editor for one exact definition.</summary>
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
    private MudForm? _policyForm;
    private bool _policyInputsValid = true;
    private bool _hasInvalidTimeoutDraft;
    private decimal? _invalidTimeoutMinutes;
    private IConverter<int?, string?> _concurrencyConverter = null!;
    private IConverter<int?, string?> _retryConverter = null!;
    private IConverter<int?, string?> _recordCountConverter = null!;
    private IConverter<int?, string?> _retentionDaysConverter = null!;
    private IConverter<decimal?, string?> _timeoutConverter = null!;

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

    /// <summary>Gets whether this editor presents only the recurring schedule while preserving every other policy override.</summary>
    [Parameter] public bool ScheduleOnly { get; set; }

    private bool IsDisposed => _lifetime.IsRetired;

    protected override void OnInitialized()
    {
        _concurrencyConverter = CreateNumericConverter<int>(NumberStyles.Integer, "Policy:Validation:WholeNumber");
        _retryConverter = CreateNumericConverter<int>(NumberStyles.Integer, "Policy:Validation:WholeNumber");
        _recordCountConverter = CreateNumericConverter<int>(NumberStyles.Integer, "Policy:Validation:WholeNumber");
        _retentionDaysConverter = CreateNumericConverter<int>(NumberStyles.Integer, "Policy:Validation:WholeNumber");
        _timeoutConverter = CreateNumericConverter<decimal>(NumberStyles.Float, "Policy:Validation:Number");
    }

    private IConverter<TNumber?, string?> CreateNumericConverter<TNumber>(NumberStyles styles, string errorKey)
        where TNumber : struct, INumber<TNumber>
    {
        string? invalidText = null;
        return Conversions.From<TNumber?, string?>(
            // MudNumericField reformats on blur; keep failed text until the user corrects or clears it.
            value => invalidText ?? value?.ToString(null, CultureInfo.InvariantCulture),
            text =>
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    invalidText = null;
                    return null;
                }

                if (TNumber.TryParse(text, styles, CultureInfo.InvariantCulture, out var value))
                {
                    invalidText = null;
                    return value;
                }

                invalidText = text;
                throw new FormatException(L[errorKey]);
            });
    }

    protected override void OnParametersSet() => _lifetime.TryApply(() =>
        _state ??= JobPolicyEditorState.Create(Definition, OperationalSummary, ObservedAtUtc ?? TimeProvider.GetUtcNow()));

    private async Task SaveAsync()
    {
        if (IsDisposed || _isSaving) return;
        if (!ScheduleOnly && _policyForm is { } form)
        {
            await form.ValidateAsync();
            if (IsDisposed || !form.IsValid || !IsTimeoutInputValid) return;
        }

        PolicySaveRequest? request = null;
        _lifetime.TryApply(() =>
        {
            if (_isSaving || _state is not { } state || !CanSaveDraft) return;
            request = new(state, state.Definition.Id,
                ScheduleOnly ? state.CreateChange() : state.CreateOperationalPolicyChange());
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
            request.State.RebaseAfterConflict(latest.Definition, latest, observedAt, operationalPolicyOnly: !ScheduleOnly);
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
    private void SetMaxConcurrency(int? value) => UpdateDraft(state => state.MaxConcurrencyOverride = value);
    private void SetRetryCount(int? value) => UpdateDraft(state => state.RetryCountOverride = value);
    private void SetMaxRecords(int? value) => UpdateDraft(state => state.MaxRetainedHistoryRecords = value);
    private void SetMaxRetentionDays(int? value) => UpdateDraft(state => state.MaxRetentionDays = value);
    private void DraftChanged() => UpdateDraft(static _ => { });
    private void DiscardScheduleDraft() => UpdateDraft(static state => state.DiscardScheduleDraft());

    private bool IsTimeoutInputValid => !_hasInvalidTimeoutDraft && (_state?.IsTimeoutValid ?? true);

    private decimal? TimeoutOverrideMinutes => _hasInvalidTimeoutDraft
        ? _invalidTimeoutMinutes
        : _state is { HasTimeoutOverride: true } state
            ? state.EffectiveTimeout.Ticks / (decimal)TimeSpan.TicksPerMinute
            : null;

    private void SetTimeoutOverrideMinutes(decimal? value) => UpdateDraft(state =>
    {
        _hasInvalidTimeoutDraft = !state.TrySetTimeoutMinutes(value);
        _invalidTimeoutMinutes = _hasInvalidTimeoutDraft ? value : null;
    });

    private int PolicyChangeCount => _state?.OperationalPolicyChangeCount ?? 0;

    private bool CanSaveDraft => _state is { } state
        && (ScheduleOnly ? state.CanSave : state.CanSaveOperationalPolicy);

    private string EnabledLabel(bool enabled) => enabled ? L["Policy:Values:Enabled"] : L["Policy:Values:Disabled"];
    private string FormatDuration(TimeSpan value) => TimePresentation.FormatDuration(value);
    private void Cancel() { if (!IsDisposed && !_isSaving) MudDialog.Cancel(); }

    /// <summary>Cancels and drains the admitted save, then releases its draft; concurrent calls share actual completion.</summary>
    public ValueTask DisposeAsync() => _lifetime.RetireAsync(() => _state = null);

    private sealed record PolicySaveRequest(JobPolicyEditorState State, JobId JobId, JobPolicyChange Change);
}
