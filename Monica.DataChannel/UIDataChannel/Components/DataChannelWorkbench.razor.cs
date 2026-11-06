using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core.Results;
using Monica.DataChannel.Facades;
using Monica.DataChannel.Localization;
using Monica.DataChannel.Middlewares;
using Monica.DataChannel.UIDataChannel.Dialogs;
using Monica.DataChannel.UIDataChannel.Models;
using Monica.DataChannel.UIDataChannel.State;
using MudBlazor;

namespace Monica.DataChannel.UIDataChannel.Components;

/// <summary>A routed DataChannel workflow borrowing circuit evidence and owning admitted work and native dialogs.</summary>
public partial class DataChannelWorkbench : IAsyncDisposable
{
    [Inject] private DataChannelWorkbenchState State { get; set; } = null!;
    [Inject] private DataChannelFacade Facade { get; set; } = null!;
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IStringLocalizer<DataChannelResource> L { get; set; } = null!;
    [Inject] private ILogger<DataChannelWorkbench> Logger { get; set; } = null!;

    /// <summary>Gets or sets whether this route presents retained exception evidence instead of the channel directory.</summary>
    [Parameter] public bool Exceptions { get; set; }

    /// <summary>Gets or sets an exact channel identity selected by a portable route link.</summary>
    [Parameter] public string? ChannelId { get; set; }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly object _ownershipGate = new();
    private readonly HashSet<TaskCompletionSource> _admitted = [];
    private readonly HashSet<IDialogReference> _dialogs = [];
    private readonly HashSet<string> _pendingChannels = new(StringComparer.Ordinal);
    private Task? _dispose;
    private bool _retired;

    /// <summary>Creates a component-local lifetime. Injected circuit state and host services remain borrowed.</summary>
    public DataChannelWorkbench() => _token = _lifetime.Token;

    protected override Task OnInitializedAsync() => RefreshAsync();

    private bool IsChannelBusy(string channelId) => State.IsRefreshing || _pendingChannels.Contains(channelId);

    private Task RefreshAsync() => RunOwned(State.RefreshAsync);

    private Task InspectPipelineAsync(DataChannelStatusInfo channel) => RunOwned(async token =>
    {
        var exactId = channel.Id;
        await ShowOwnedAsync<ChannelPipelineDialog>(L["Workbench:PipelineDialogTitle"].Value, new DialogParameters
        {
            [nameof(ChannelPipelineDialog.Channel)] = channel,
            [nameof(ChannelPipelineDialog.OnOpenDebugger)] = (Func<PipelineComponentInfo, CancellationToken, Task>)
                ((component, ownerToken) => OpenDebuggerAsync(exactId, component, ownerToken)),
            [nameof(ChannelPipelineDialog.OnReinitialize)] = (Func<string, CancellationToken, Task>)ReinitializeAsync,
            [nameof(ChannelPipelineDialog.OnViewExceptions)] = (Func<string, CancellationToken, Task>)InspectExceptionsAsync
        }, token);
        if (!_retired) await State.RefreshAsync(token);
    });

    private Task OpenDebuggerAsync(string channelId, PipelineComponentInfo component, CancellationToken ownerToken) => RunOwned(async token =>
    {
        var result = await Facade.GetMiddlewareByKeyAsync<MessageDebuggerMiddleware>(channelId, component.ComponentKey);
        token.ThrowIfCancellationRequested();
        if (result.IsFailure() || result.Data is not { } middleware)
        {
            Snackbar.Add(result.Message ?? L["Workbench:OperationFailed"].Value, Severity.Error);
            return;
        }
        await ShowOwnedAsync<MessageDebuggerDialog>(L["Workbench:DebuggerDialogTitle"].Value, new DialogParameters
        {
            [nameof(MessageDebuggerDialog.Middleware)] = middleware,
            [nameof(MessageDebuggerDialog.ChannelId)] = channelId
        }, token);
    }, ownerToken);

    private Task InspectExceptionsAsync(string channelId) => InspectExceptionsAsync(channelId, default);

    private Task InspectExceptionsAsync(string channelId, CancellationToken ownerToken) => RunOwned(async token =>
    {
        var result = await Facade.GetChannelExceptionsAsync(channelId, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (result.IsFailure() || result.Data is not { } details)
        {
            Snackbar.Add(result.Message ?? L["Workbench:OperationFailed"].Value, Severity.Error);
            return;
        }
        await ShowOwnedAsync<ExceptionDetailsDialog>(L["Exceptions:Details:Title"].Value, new DialogParameters
        {
            [nameof(ExceptionDetailsDialog.ChannelId)] = channelId,
            [nameof(ExceptionDetailsDialog.ExceptionInfo)] = details
        }, token);
    }, ownerToken);

    private Task ReinitializeAsync(string channelId) => ReinitializeAsync(channelId, default);

    private Task ReinitializeAsync(string channelId, CancellationToken ownerToken) => ExecuteChannelAsync(channelId, async token =>
    {
        var outcome = await State.ReinitializeAsync(channelId, token);
        Report(outcome, "Workbench:ReinitializeSucceeded", channelId, token);
        await State.RefreshAsync(token);
    }, ownerToken);

    private Task ClearHistoryAsync(string channelId) => ExecuteChannelAsync(channelId, async token =>
    {
        var exactId = channelId;
        var confirmation = await ShowOwnedAsync<ClearChannelHistoryDialog>(L["Workbench:ClearHistoryTitle"].Value,
            new DialogParameters { [nameof(ClearChannelHistoryDialog.ChannelId)] = exactId }, token, MaxWidth.Small);
        token.ThrowIfCancellationRequested();
        if (confirmation is not { Canceled: false, Data: true }) return;
        var outcome = await State.ClearHistoryAsync(exactId, token);
        Report(outcome, "Workbench:HistoryCleared", exactId, token);
        await State.RefreshAsync(token);
    });

    private Task ExecuteChannelAsync(string channelId, Func<CancellationToken, Task> action, CancellationToken ownerToken = default)
    {
        return RunOwned(async token =>
        {
            lock (_ownershipGate)
            {
                if (!_pendingChannels.Add(channelId)) return;
            }
            try { await action(token); }
            finally
            {
                lock (_ownershipGate) { _pendingChannels.Remove(channelId); }
                if (!_retired) await InvokeAsync(StateHasChanged);
            }
        }, ownerToken);
    }

    private void Report(Result result, string successKey, string channelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.IsFailure()) Snackbar.Add(result.Message ?? L["Workbench:OperationFailed"].Value, Severity.Error);
        else Snackbar.Add(L[successKey, channelId].Value, Severity.Success);
    }

    private async Task<DialogResult?> ShowOwnedAsync<T>(string title, DialogParameters parameters,
        CancellationToken cancellationToken, MaxWidth maxWidth = MaxWidth.Large)
        where T : IComponent
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reference = await DialogService.ShowAsync<T>(title, parameters,
            new DialogOptions { FullWidth = true, MaxWidth = maxWidth, CloseButton = true, CloseOnEscapeKey = true });
        lock (_ownershipGate)
        {
            if (_retired || cancellationToken.IsCancellationRequested) { CancelDialog(reference); return null; }
            _dialogs.Add(reference);
        }
        // Native acquisition has no cancellation parameter. Observe it completely, then close any
        // published child as soon as its route or originating inspector retires.
        await using var cancellation = cancellationToken.Register(() => CancelDialog(reference));
        try { return await reference.Result; }
        finally { lock (_ownershipGate) { _dialogs.Remove(reference); } }
    }

    private Task RunOwned(Func<CancellationToken, Task> action, CancellationToken ownerToken = default)
    {
        TaskCompletionSource completion;
        lock (_ownershipGate)
        {
            if (_retired || ownerToken.IsCancellationRequested) return Task.CompletedTask;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _admitted.Add(completion);
        }
        return RunAdmissionAsync(action, completion, ownerToken);
    }

    private async Task RunAdmissionAsync(Func<CancellationToken, Task> action, TaskCompletionSource completion,
        CancellationToken ownerToken)
    {
        using var linkedLifetime = ownerToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_token, ownerToken)
            : null;
        var cancellationToken = linkedLifetime?.Token ?? _token;
        try { cancellationToken.ThrowIfCancellationRequested(); await action(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Logger.LogError(exception, "DataChannel workbench operation failed");
            if (!_retired && !cancellationToken.IsCancellationRequested)
                Snackbar.Add(L["Workbench:OperationFailed"].Value, Severity.Error);
        }
        finally
        {
            lock (_ownershipGate) { _admitted.Remove(completion); }
            completion.TrySetResult();
        }
    }

    /// <summary>Closes all owned native dialogs and drains actual admitted work before disposing this component lifetime.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_ownershipGate)
        {
            _retired = true;
            return new ValueTask(_dispose ??= RetireAsync());
        }
    }

    private async Task RetireAsync()
    {
        var failures = new List<Exception>();
        // Native dialog callbacks must run on this renderer-owned disposal context.
        try { _lifetime.Cancel(); }
        catch (Exception exception) { failures.Add(exception); }
        IDialogReference[] dialogs;
        Task[] pending;
        lock (_ownershipGate)
        {
            dialogs = _dialogs.ToArray();
            pending = _admitted.Select(admission => admission.Task).ToArray();
        }
        foreach (var reference in dialogs)
        {
            try { CancelDialog(reference); }
            catch (Exception exception) { failures.Add(exception); }
        }
        foreach (var operation in pending) await ObserveAsync(operation);
        _lifetime.Dispose();
        if (failures.Count > 0) throw new AggregateException("DataChannel workbench retirement failed.", failures);

        async Task ObserveAsync(Task operation)
        {
            try { await operation; }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception exception) { failures.Add(exception); }
        }
    }

    private static void CancelDialog(IDialogReference reference)
    {
        var result = DialogResult.Cancel();
        try { reference.Close(result); }
        finally { reference.Dismiss(result); }
    }
}
