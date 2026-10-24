using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Monica.Core.Results;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.UI.Components;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Executions.Support;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using MudBlazor;

namespace Monica.JobScheduler.UI.Pages;

/// <summary>Composes one exact definition's immutable contract, editable policy and bounded operational evidence.</summary>
/// <remarks>
/// Every route creates its own state and native dialog owner. Dialog requests freeze their exact definition and
/// observation before an incomplete await; route retirement closes and drains actual native results. Modal
/// openers remain focusable while duplicate admission is rejected by the dialog owner.
/// </remarks>
public partial class JobDefinitionDetailPage : IAsyncDisposable
{
    /// <summary>Gets the route pattern for one owner-scoped job definition.</summary>
    public const string PAGE_URL = "/job-scheduler/catalog/{OwnerKey}/{JobKey}";

    /// <summary>Gets or sets the exact route-provided owner key without trimming or case normalization.</summary>
    [Parameter] public string OwnerKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the exact route-provided logical job key without trimming or case normalization.</summary>
    [Parameter] public string JobKey { get; set; } = string.Empty;

    [Inject] private JobDefinitionDetailPageStateFactory PageStateFactory { get; set; } = null!;
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private IStringLocalizer<JobSchedulerResource> L { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private SchedulerTimePresentation TimePresentation { get; set; } = null!;
    [Inject] private TimeProvider TimeProvider { get; set; } = null!;

    private readonly SchedulerOperationLifetime _lifetime = new();
    private readonly object _disposeGate = new();
    private TaskCompletionSource? _retirement;
    private JobDefinitionDetailPageState? PageState { get; set; }
    private SchedulerDialogSession<DetailDialogRequest>? _dialogs;
    private Func<Task>? _stateChangedHandler;
    private int _stateVersion;
    private bool _disposed;
    private object? _commandAdmission;

    private string PageTitleText => PageState?.Summary?.Definition.EffectiveConfiguration.JobName ?? L["JobDetail:PageTitle"];

    protected override async Task OnParametersSetAsync()
    {
        if (_disposed) return;
        var jobId = new JobId(OwnerKey, JobKey);
        if (PageState?.JobId == jobId) return;
        try { await _lifetime.RunAsync(_ => ReplaceStateAsync(jobId)); }
        catch (OperationCanceledException) when (_disposed) { }
    }

    private async Task ReplaceStateAsync(JobId jobId)
    {
        var version = Interlocked.Increment(ref _stateVersion);
        var previousState = PageState;
        var previousHandler = _stateChangedHandler;
        var previousDialogs = _dialogs;
        PageState = null;
        _stateChangedHandler = null;
        _dialogs = null;
        _commandAdmission = null;
        await RetireRouteAsync(previousState, previousHandler, previousDialogs);
        if (!IsCurrentStateVersion(version)) return;

        var state = PageStateFactory.Create(jobId);
        var dialogs = new SchedulerDialogSession<DetailDialogRequest>(ProduceDialogAsync, CompleteDialogAsync);
        Func<Task> changed = () => HandleStateChangedAsync(state, version);
        state.Changed += changed;
        if (!IsCurrentStateVersion(version))
        {
            await RetireRouteAsync(state, changed, dialogs);
            return;
        }
        PageState = state;
        _stateChangedHandler = changed;
        _dialogs = dialogs;
        try { await state.InitializeAsync(); }
        catch (OperationCanceledException) when (!IsCurrentStateVersion(version) || !ReferenceEquals(PageState, state)) { }
        if (!IsCurrentStateVersion(version) || !ReferenceEquals(PageState, state))
        {
            if (ReferenceEquals(PageState, state))
            {
                PageState = null;
                _stateChangedHandler = null;
                _dialogs = null;
            }
            await RetireRouteAsync(state, changed, dialogs);
        }
    }

    private Task OpenScheduleInspectorAsync() => OpenPolicyAsync();
    private Task OpenPolicyAsync() => OpenDialogAsync(DetailDialogKind.Policy);
    private Task OpenTriggerAsync() => OpenDialogAsync(DetailDialogKind.Trigger);
    private Task OpenExecutionDetailsAsync(string instanceId) => string.IsNullOrWhiteSpace(instanceId)
        ? Task.CompletedTask : OpenDialogAsync(DetailDialogKind.Execution, instanceId);

    private Task OpenDialogAsync(DetailDialogKind kind, string? instanceId = null)
    {
        var state = PageState;
        var dialogs = _dialogs;
        if (_disposed || state?.Summary is not { } summary || dialogs is null || state.IsMutating || _commandAdmission is not null)
            return Task.CompletedTask;
        if (kind == DetailDialogKind.Trigger && (!summary.Definition.IsPresent || summary.Definition.IsDisabled)) return Task.CompletedTask;
        return dialogs.OpenAsync(new(state, _stateVersion, summary, state.ObservedAtUtc, kind, instanceId));
    }

    private Task<IDialogReference> ProduceDialogAsync(DetailDialogRequest request) => request.Kind switch
    {
        DetailDialogKind.Execution => ExecutionDetailDialogLauncher.OpenAsync(DialogService, L, request.InstanceId!),
        DetailDialogKind.Policy => DialogService.ShowAsync<JobPolicyDialog>(L["Policy:Title"],
            new DialogParameters<JobPolicyDialog>
            {
                { dialog => dialog.Definition, request.Summary.Definition },
                { dialog => dialog.OperationalSummary, request.Summary },
                { dialog => dialog.ObservedAtUtc, request.ObservedAtUtc }
            }, WorkbenchDialogOptions),
        DetailDialogKind.Trigger => DialogService.ShowAsync<JobTriggerDialog>(L["Trigger:Title"],
            new DialogParameters<JobTriggerDialog> { { dialog => dialog.Definition, request.Summary.Definition } }, SmallDialogOptions),
        _ => throw new ArgumentOutOfRangeException(nameof(request))
    };

    private async Task CompleteDialogAsync(DetailDialogRequest request, DialogResult? result)
    {
        if (!IsCurrentStateVersion(request.Version) || !ReferenceEquals(PageState, request.State)) return;
        var refresh = request.Kind == DetailDialogKind.Execution;
        if (request.Kind == DetailDialogKind.Policy && result is { Canceled: false, Data: not null })
        {
            Snackbar.Add(L["Policy:Saved"], Severity.Success);
            refresh = true;
        }
        if (request.Kind == DetailDialogKind.Trigger && result is { Canceled: false, Data: JobExecutionInstance execution })
        {
            Snackbar.Add(L["Trigger:Queued", execution.InstanceId], Severity.Success);
            refresh = true;
        }
        if (refresh)
        {
            try { await request.State.RefreshAsync(); }
            catch (OperationCanceledException) when (!IsCurrentStateVersion(request.Version) || !ReferenceEquals(PageState, request.State)) { }
        }
    }

    private Task RefreshAsync() => RunCommandAsync(static state => state.RefreshAsync());

    private Task ToggleDisabledAsync() => RunCommandAsync(async state =>
    {
        if (state.Summary is not { } summary) return;
        var operatorPaused = JobSchedulerUiPresentation.IsOperatorPaused(summary);
        var manualSuppressed = JobSchedulerUiPresentation.IsManualSuppressed(summary);
        if (!operatorPaused && manualSuppressed)
        {
            Snackbar.Add(L["Catalog:Messages:PauseUnavailableManual"], Severity.Info);
            return;
        }
        var disabled = !operatorPaused;
        var result = await state.SetDisabledAsync(disabled);
        if (_disposed || !ReferenceEquals(PageState, state)) return;
        if (result.IsFailed(out var error))
        {
            Snackbar.Add(error.Message ?? L["Catalog:Messages:UpdateFailed"], Severity.Error);
            return;
        }
        var message = !disabled && manualSuppressed ? L["Catalog:Messages:ResumedManualRemains"]
            : disabled ? L["Catalog:Messages:Paused"] : L["Catalog:Messages:Resumed"];
        Snackbar.Add(message, !disabled && manualSuppressed ? Severity.Info : Severity.Success);
    });

    private Task RunRecurringNowAsync() => RunCommandAsync(async state =>
    {
        if (state.Summary is not { Definition.IsPresent: true }) return;
        var result = await state.RunRecurringNowAsync();
        if (_disposed || !ReferenceEquals(PageState, state)) return;
        if (result.IsFailed(out var error, out var execution))
        {
            Snackbar.Add(error.Message ?? L["Catalog:Messages:RunFailed"], Severity.Error);
            return;
        }
        Snackbar.Add(L["Catalog:Messages:Queued", execution.InstanceId], Severity.Success);
        await state.RefreshAsync();
    });

    private async Task RunCommandAsync(Func<JobDefinitionDetailPageState, Task> command)
    {
        var state = PageState;
        if (_disposed || state is null || _commandAdmission is not null || state.IsLoading || state.IsMutating) return;
        var admission = new object();
        _commandAdmission = admission;
        try { await _lifetime.RunAsync(_ => command(state)); }
        catch (OperationCanceledException) when (_disposed || !ReferenceEquals(PageState, state)) { }
        finally { if (ReferenceEquals(_commandAdmission, admission)) _commandAdmission = null; }
    }

    private async Task HandleStateChangedAsync(JobDefinitionDetailPageState state, int version)
    {
        if (!IsCurrentStateVersion(version) || !ReferenceEquals(PageState, state)) return;
        try
        {
            await _lifetime.RunAsync(_ => InvokeAsync(() =>
            {
                if (IsCurrentStateVersion(version) && ReferenceEquals(PageState, state)) StateHasChanged();
            }));
        }
        catch (OperationCanceledException) when (_disposed) { }
    }

    private bool IsCurrentStateVersion(int version) => !_disposed && version == Volatile.Read(ref _stateVersion);

    private static DialogOptions SmallDialogOptions { get; } = new()
    { CloseButton = true, CloseOnEscapeKey = true, FullWidth = true, MaxWidth = MaxWidth.Small };
    private static DialogOptions WorkbenchDialogOptions { get; } = new()
    { CloseButton = true, CloseOnEscapeKey = true, FullWidth = true, MaxWidth = MaxWidth.ExtraLarge };

    private static async Task RetireRouteAsync(JobDefinitionDetailPageState? state, Func<Task>? changed,
        SchedulerDialogSession<DetailDialogRequest>? dialogs)
    {
        if (state is not null && changed is not null) state.Changed -= changed;
        var failures = new List<Exception>();
        Task? dialogRetirement = null;
        Task? stateRetirement = null;
        if (dialogs is not null)
        {
            try { dialogRetirement = dialogs.DisposeAsync().AsTask(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (state is not null)
        {
            try { stateRetirement = state.DisposeAsync().AsTask(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        // Start both owners before draining: an admitted dialog callback may already be refreshing the state.
        foreach (var retirement in new[] { dialogRetirement, stateRetirement })
        {
            if (retirement is null) continue;
            try { await retirement; }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count != 0) throw new AggregateException("Job definition route retirement failed.", failures);
    }

    /// <summary>Retires the route, closes and drains native dialogs, and observes every admitted page operation exactly once.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_disposeGate)
        {
            if (_retirement is not null) return new(_retirement.Task);
            _disposed = true;
            Interlocked.Increment(ref _stateVersion);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = completion;
        }
        _ = CompleteRetirementAsync(completion);
        return new(completion.Task);
    }

    private async Task CompleteRetirementAsync(TaskCompletionSource completion)
    {
        var state = PageState;
        var changed = _stateChangedHandler;
        var dialogs = _dialogs;
        PageState = null;
        _stateChangedHandler = null;
        _dialogs = null;
        var lifetime = _lifetime.RetireAsync(static () => { }).AsTask();
        var failures = new List<Exception>();
        try { await RetireRouteAsync(state, changed, dialogs); }
        catch (Exception exception) { failures.Add(exception); }
        try { await lifetime; }
        catch (Exception exception) { failures.Add(exception); }
        if (failures.Count == 0) completion.TrySetResult();
        else completion.TrySetException(new AggregateException("Job definition page retirement failed.", failures));
    }

    private enum DetailDialogKind { Policy, Trigger, Execution }
    private sealed record DetailDialogRequest(JobDefinitionDetailPageState State, int Version,
        JobOperationalSummary Summary, DateTimeOffset? ObservedAtUtc, DetailDialogKind Kind, string? InstanceId);
}
