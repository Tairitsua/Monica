using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.Pages;
using Monica.JobScheduler.UI.UIJobScheduler.Components;
using Monica.JobScheduler.UI.UIJobScheduler.Executions.State;
using Monica.JobScheduler.UI.UIJobScheduler.Executions.Support;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using MudBlazor;

namespace Monica.JobScheduler.UI.Components;

/// <summary>Inspects one exact durable execution and owns its native confirmation, clipboard, and async lifetime.</summary>
public partial class ExecutionDetailDialog : IAsyncDisposable
{
    [Inject] private JobExecutionsStateFactory PageStateFactory { get; set; } = null!;
    [Inject] private IStringLocalizer<JobSchedulerResource> L { get; set; } = null!;
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private SchedulerTimePresentation TimePresentation { get; set; } = null!;
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;

    /// <summary>
    /// Gets or sets the exact, opaque execution ID loaded once for this native dialog owner.
    /// A caller must open a new dialog for a different identity; whitespace and reserved characters are preserved.
    /// </summary>
    [Parameter, EditorRequired]
    public string InstanceId { get; set; } = string.Empty;

    private readonly SchedulerOperationLifetime _lifetime = new();
    private readonly object _retirementGate = new();
    private TaskCompletionSource? _retirement;
    private ExecutionDetailState? _state;
    private SchedulerDialogSession<CancellationRequest>? _confirmations;
    private bool _retainCancellationOpener;

    protected override async Task OnInitializedAsync()
    {
        _confirmations = new(OpenConfirmationAsync, CompleteCancellationAsync);
        var state = PageStateFactory.CreateDetailState(InstanceId);
        _state = state;
        state.StateChanged += OnStateChangedAsync;
        try { await _lifetime.RunAsync(_ => state.InitializeAsync()); }
        catch (OperationCanceledException) when (_lifetime.IsRetired) { }
    }

    private Task OnStateChangedAsync() => _lifetime.IsRetired ? Task.CompletedTask : InvokeAsync(() =>
    {
        if (!_lifetime.IsRetired) StateHasChanged();
    });

    private async Task RefreshAsync()
    {
        if (_state is not { } state || state.IsLoading || _lifetime.IsRetired) return;
        try { await _lifetime.RunAsync(_ => state.RefreshAsync()); }
        catch (OperationCanceledException) when (_lifetime.IsRetired || state.IsDisposed) { }
    }

    private async Task CopyDiagnosticsAsync()
    {
        if (_state is not { Execution: { } execution, ObservedAtUtc: { } observedAtUtc } state || _lifetime.IsRetired) return;
        var snapshot = ExecutionUiPresentation.ExportDiagnostics(execution, observedAtUtc);
        try
        {
            await _lifetime.RunAsync(async cancellationToken =>
            {
                if (!await state.ReauthorizeAsync() || _lifetime.IsRetired) return;
                bool copied;
                try { copied = await JsRuntime.InvokeAsync<bool>("MoClipboard.copyText", cancellationToken, snapshot); }
                catch (JSException) { copied = false; }
                catch (JSDisconnectedException)
                {
                    if (_lifetime.IsRetired) return;
                    copied = false;
                }
                if (!_lifetime.IsRetired)
                    Snackbar.Add(copied ? L["ExecutionDetail:Copied"] : L["ExecutionDetail:CopyFailed"],
                        copied ? Severity.Success : Severity.Warning);
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsRetired || state.IsDisposed) { }
    }

    private async Task CancelAsync(JobExecutionInstance execution)
    {
        var state = _state;
        var exactId = execution.InstanceId;
        if (state is null || state.IsMutating || execution.IsTerminal || _confirmations is null || _lifetime.IsRetired) return;
        // Keep the actual native confirmation opener available when cancellation makes the record terminal.
        _retainCancellationOpener = true;
        try
        {
            if (!await state.ReauthorizeAsync() || _lifetime.IsRetired || _state != state) return;
            await _confirmations.OpenAsync(new(state, exactId));
        }
        catch (OperationCanceledException) when (_lifetime.IsRetired || state.IsDisposed) { }
    }

    private Task<IDialogReference> OpenConfirmationAsync(CancellationRequest request)
    {
        var parameters = new DialogParameters<MudMessageBox>
        {
            { box => box.Title, L["Executions:Cancel:Title"].Value },
            { box => box.MessageContent, (RenderFragment)(builder =>
                {
                    builder.OpenComponent<ExecutionCancellationMessage>(0);
                    builder.AddAttribute(1, nameof(ExecutionCancellationMessage.Message), L["Executions:Cancel:Message", request.InstanceId].Value);
                    builder.CloseComponent();
                }) },
            { box => box.YesText, L["Executions:Actions:Cancel"].Value },
            { box => box.CancelText, L["Common:Keep"].Value }
        };
        return DialogService.ShowAsync<MudMessageBox>(null, parameters,
            new DialogOptions { CloseButton = true, CloseOnEscapeKey = true, FullWidth = true, MaxWidth = MaxWidth.Small });
    }

    private async Task CompleteCancellationAsync(CancellationRequest request, DialogResult? confirmation)
    {
        if (_lifetime.IsRetired || _state != request.State || confirmation is not { Canceled: false, Data: true }) return;
        try
        {
            // The state owns one immutable identity; verify it still matches the actual native request.
            if (!string.Equals(request.InstanceId, request.State.InstanceId, StringComparison.Ordinal)) return;
            var result = await request.State.RequestCancellationAsync();
            if (_lifetime.IsRetired) return;
            var feedback = ExecutionUiPresentation.GetCancellationFeedback(result, L);
            Snackbar.Add(feedback.Message, feedback.Severity);
        }
        catch (OperationCanceledException) when (_lifetime.IsRetired || request.State.IsDisposed) { }
    }

    private void OpenJobDetails()
    {
        if (_lifetime.IsRetired) return;
        var template = _state?.Execution?.Template;
        var returnUrl = Navigation.ToAbsoluteUri(Navigation.Uri).PathAndQuery;
        MudDialog.Close();
        Navigation.NavigateTo(template is null ? JobCatalogPage.PAGE_URL
            : JobSchedulerUiUrls.JobDefinition(new JobId(template.OwnerKey, template.JobKey), returnUrl));
    }

    private void Close() => MudDialog.Close();

    private void OpenExecutionLedger()
    {
        if (_lifetime.IsRetired || _state?.Execution is not { } execution) return;
        var url = $"{JobExecutionsPage.PAGE_URL}?instanceId={Uri.EscapeDataString(execution.InstanceId)}&owner={Uri.EscapeDataString(execution.Template.OwnerKey)}&job={Uri.EscapeDataString(execution.Template.JobKey)}";
        MudDialog.Close();
        Navigation.NavigateTo(url);
    }

    private async Task CopyInstanceIdAsync()
    {
        if (_state is not { } state || _lifetime.IsRetired) return;
        var exactId = InstanceId;
        try
        {
            await _lifetime.RunAsync(async token =>
            {
                if (!await state.ReauthorizeAsync() || _lifetime.IsRetired) return;
                var copied = await JsRuntime.InvokeAsync<bool>("MoClipboard.copyText", token, exactId);
                if (!_lifetime.IsRetired)
                    Snackbar.Add(L[copied ? "Workspace:IdCopied" : "ExecutionDetail:CopyFailed"], copied ? Severity.Success : Severity.Warning);
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsRetired || state.IsDisposed) { }
        catch (JSException) { if (!_lifetime.IsRetired) Snackbar.Add(L["ExecutionDetail:CopyFailed"], Severity.Warning); }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_retirementGate)
        {
            if (_retirement is not null) return new(_retirement.Task);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = completion;
        }
        var state = _state;
        if (state is not null) state.StateChanged -= OnStateChangedAsync;
        var operations = _lifetime.RetireAsync(() => ValueTask.CompletedTask);
        var confirmations = _confirmations?.DisposeAsync() ?? ValueTask.CompletedTask;
        var stateRetirement = state?.DisposeAsync() ?? ValueTask.CompletedTask;
        _ = CompleteRetirementAsync([operations, confirmations, stateRetirement], completion);
        return new(completion.Task);
    }

    private static async Task CompleteRetirementAsync(ValueTask[] owners, TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        foreach (var owner in owners)
        {
            try { await owner; } catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count == 0) completion.TrySetResult();
        else completion.TrySetException(new AggregateException("Execution inspector retirement failed.", failures));
    }

    private sealed record CancellationRequest(ExecutionDetailState State, string InstanceId);
}
