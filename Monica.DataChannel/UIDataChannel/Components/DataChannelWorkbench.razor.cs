using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core.Results;
using Monica.DataChannel.Facades;
using Monica.DataChannel.Localization;
using Monica.DataChannel.Middlewares;
using Monica.DataChannel.UIDataChannel.Dialogs;
using Monica.DataChannel.UIDataChannel.Models;
using Monica.DataChannel.UIDataChannel.State;
using Monica.DataChannel.UIDataChannel.Support;
using Monica.DataChannel.Pages;
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
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    /// <summary>Gets or sets whether this route presents retained exception evidence instead of the channel directory.</summary>
    [Parameter] public bool Exceptions { get; set; }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly object _ownershipGate = new();
    private readonly HashSet<TaskCompletionSource> _admitted = [];
    private readonly HashSet<IDialogReference> _dialogs = [];
    private readonly HashSet<string> _pendingChannels = new(StringComparer.Ordinal);
    private Task? _dispose;
    private bool _retired;
    private bool _debuggerOpen;
    private DataChannelManagementSnapshot? _capture;
    private DataChannelView? _view;
    private DataChannelQuery Query { get; set; } = new();
    private DataChannelView? View
    {
        get
        {
            if (!ReferenceEquals(_capture, State.Snapshot))
            {
                _capture = State.Snapshot;
                _view = _capture is null ? null : new DataChannelView(_capture);
            }
            return _view;
        }
    }

    /// <summary>Creates a component-local lifetime. Injected circuit state and host services remain borrowed.</summary>
    public DataChannelWorkbench() => _token = _lifetime.Token;

    protected override Task OnInitializedAsync()
    {
        Query = DataChannelQuery.Read(Navigation.ToAbsoluteUri(Navigation.Uri));
        Navigation.LocationChanged += OnLocationChanged;
        return State.Snapshot is null ? RefreshAsync() : Task.CompletedTask;
    }

    private bool IsChannelBusy(string channelId) => State.IsRefreshing || _pendingChannels.Contains(channelId);
    private bool SetupBusy => State.IsRefreshing || _pendingChannels.Count > 0;
    private string ScopeUrl(bool exceptions) => new DataChannelQuery { Channel = Query.Channel }.Url(exceptions);

    private Task RefreshAsync() => RunOwned(State.RefreshAsync);

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        _ = InvokeAsync(() =>
        {
            if (_retired) return;
            var uri = Navigation.ToAbsoluteUri(args.Location);
            var ownPath = Exceptions ? UIDataChannelExceptionsPage.PAGE_URL : UIDataChannelPage.PAGE_URL;
            if (!uri.AbsolutePath.TrimEnd('/').Equals(ownPath, StringComparison.OrdinalIgnoreCase)) return;
            Query = DataChannelQuery.Read(uri);
            StateHasChanged();
        });
    }

    private void ChangeFilter(DataChannelQuery next) =>
        Navigate(next, replace: Query.Inspect == next.Inspect);

    private void CloseSheet() => Navigate(Query.Close(), replace: true);

    private void Navigate(DataChannelQuery next, bool replace)
    {
        if (Query == next) return;
        Navigation.NavigateTo(next.Url(Exceptions), replace: replace);
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (_retired || _debuggerOpen || !Query.Debugger || Query.Inspect is not { } id ||
            View?.ById.GetValueOrDefault(id) is not { } channel)
        {
            return Task.CompletedTask;
        }
        var component = Query.Component is null ? channel.Debugger :
            channel.Status.Middlewares.FirstOrDefault(item => item.CanDebugMessages && item.ComponentKey == Query.Component);
        _debuggerOpen = true;
        return RunOwned(async token =>
        {
            try
            {
                var result = component is null ? null :
                    await Facade.GetMiddlewareByKeyAsync<MessageDebuggerMiddleware>(id, component.ComponentKey);
                token.ThrowIfCancellationRequested();
                if (result?.Data is not { } middleware)
                {
                    Snackbar.Add(L["Design:DebuggerUnavailable"], Severity.Warning);
                    return;
                }
                await ShowOwnedAsync<MessageDebuggerDialog>(L["Workbench:DebuggerDialogTitle"].Value, new DialogParameters
                {
                    [nameof(MessageDebuggerDialog.Middleware)] = middleware,
                    [nameof(MessageDebuggerDialog.ChannelId)] = id
                }, token, MaxWidth.False, closeButton: false);
                if (!_retired) await State.RefreshAsync(token);
            }
            finally
            {
                _debuggerOpen = false;
                if (!_retired && Query.Inspect == id && Query.Debugger)
                {
                    Navigate(Query with { Debugger = false }, replace: true);
                }
            }
        });
    }

    private Task StartSetupAsync(string setupId) => ExecuteSetupAsync(setupId, State.StartSetupAsync, "Setup:Started");
    private Task StopSetupAsync(string setupId) => ExecuteSetupAsync(setupId, State.StopSetupAsync, "Setup:Stopped");
    private Task RestartSetupAsync(string setupId) => ExecuteSetupAsync(setupId, State.RestartSetupAsync, "Setup:Restarted");

    private Task ExecuteSetupAsync(string setupId, Func<string, CancellationToken, Task<Result>> command, string successKey) =>
        ExecuteChannelAsync("setup:" + setupId, async token =>
        {
            var outcome = await command(setupId, token);
            Report(outcome, successKey, setupId, token);
            await State.RefreshAsync(token);
        });

    private Task ReinitializeAsync(string channelId) => ExecuteChannelAsync(channelId, async token =>
    {
        var outcome = await State.ReinitializeAsync(channelId, token);
        Report(outcome, "Workbench:ReinitializeSucceeded", channelId, token);
        await State.RefreshAsync(token);
    });

    private Task ClearHistoryAsync(string channelId) => ExecuteChannelAsync(channelId, async token =>
    {
        var exactId = channelId;
        var confirmation = await ShowOwnedAsync<ClearChannelHistoryDialog>(L["Workbench:ClearHistoryTitle"].Value,
            new DialogParameters { [nameof(ClearChannelHistoryDialog.ChannelId)] = exactId }, token, MaxWidth.Small,
            closeButton: false);
        token.ThrowIfCancellationRequested();
        if (confirmation is not { Canceled: false, Data: true }) return;
        var outcome = await State.ClearHistoryAsync(exactId, token);
        Report(outcome, "Workbench:HistoryCleared", exactId, token);
        await State.RefreshAsync(token);
    });

    private Task ExecuteChannelAsync(string channelId, Func<CancellationToken, Task> action)
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
        });
    }

    private void Report(Result result, string successKey, string channelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.IsFailure()) Snackbar.Add(result.Message ?? L["Workbench:OperationFailed"].Value, Severity.Error);
        else Snackbar.Add(L[successKey, channelId].Value, Severity.Success);
    }

    private async Task<DialogResult?> ShowOwnedAsync<T>(string title, DialogParameters parameters,
        CancellationToken cancellationToken, MaxWidth maxWidth = MaxWidth.Large, bool closeButton = true)
        where T : IComponent
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reference = await DialogService.ShowAsync<T>(title, parameters,
            new DialogOptions { FullWidth = true, MaxWidth = maxWidth, CloseButton = closeButton, CloseOnEscapeKey = true });
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

    private Task RunOwned(Func<CancellationToken, Task> action)
    {
        TaskCompletionSource completion;
        lock (_ownershipGate)
        {
            if (_retired) return Task.CompletedTask;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _admitted.Add(completion);
        }
        return RunAdmissionAsync(action, completion);
    }

    private async Task RunAdmissionAsync(Func<CancellationToken, Task> action, TaskCompletionSource completion)
    {
        var cancellationToken = _token;
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
        Navigation.LocationChanged -= OnLocationChanged;
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
