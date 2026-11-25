using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core.Results;
using Monica.EventBus.Models;
using Monica.EventBus.Abstractions;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.Pages;
using Monica.Framework.UI.UIEventBus.Dialogs;
using Monica.Framework.UI.UIEventBus.Models;
using Monica.Framework.UI.UIEventBus.State;
using Monica.Framework.UI.UIEventBus.Support;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.Framework.UI.UIEventBus.Components;

/// <summary>A routed EventBus diagnostic view borrowing circuit state and owning its mounted observation lifetime.</summary>
public partial class EventBusWorkbench : IAsyncDisposable
{
    [Inject] private EventBusWorkbenchState State { get; set; } = null!;
    [Inject] private EventBusMonitorService Monitor { get; set; } = null!;
    [Inject] private EventBusTestService TestService { get; set; } = null!;
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IStringLocalizer<EventBusResource> L { get; set; } = null!;
    [Inject] private ILogger<EventBusWorkbench> Logger { get; set; } = null!;

    /// <summary>Gets or sets the independently addressable diagnostic workflow.</summary>
    [Parameter] public EventBusWorkbenchView View { get; set; }
    /// <summary>Gets or sets an exact full event-type identity selected by a registry drill link.</summary>
    [Parameter] public string? EventType { get; set; }
    /// <summary>Gets or sets an exact topic selected by a registry drill link.</summary>
    [Parameter] public string? Topic { get; set; }
    /// <summary>Gets or sets an opaque provider identity selected by a registry drill link.</summary>
    [Parameter] public string? ProviderId { get; set; }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly object _ownershipGate = new();
    private readonly HashSet<TaskCompletionSource> _admitted = [];
    private readonly HashSet<IDialogReference> _dialogs = [];
    private EventBusMonitorChangeFeed? _feed;
    private Task? _reader;
    private Task? _poll;
    private Task? _dispose;
    private long _observation;
    private bool _retired;
    private bool _busy;

    /// <summary>Creates a component-local observation lifetime; DI services are borrowed and never disposed here.</summary>
    public EventBusWorkbench() => _token = _lifetime.Token;

    private string Title => View switch
    {
        EventBusWorkbenchView.Providers => L["Pages:EventBusProviders:Title"].Value,
        EventBusWorkbenchView.Subscriptions => L["Pages:EventBusSubscriptions:Title"].Value,
        EventBusWorkbenchView.Changes => L["Pages:EventBusChanges:Title"].Value,
        EventBusWorkbenchView.TestTools => L["Pages:EventBusTestTools:Title"].Value,
        _ => L["Pages:EventBusMonitor:Title"].Value
    };

    private string SectionTitle(EventBusWorkbenchView section) => section switch
    {
        EventBusWorkbenchView.Providers => L["Workbench:Providers"].Value,
        EventBusWorkbenchView.Subscriptions => L["Workbench:Subscriptions"].Value,
        EventBusWorkbenchView.Changes => L["Workbench:Changes"].Value,
        EventBusWorkbenchView.TestTools => L["Workbench:TestTools"].Value,
        _ => L["Workbench:Overview"].Value
    };

    private static string Route(EventBusWorkbenchView section) => section switch
    {
        EventBusWorkbenchView.Providers => UIEventBusProvidersPage.PAGE_URL,
        EventBusWorkbenchView.Subscriptions => UIEventBusSubscriptionsPage.PAGE_URL,
        EventBusWorkbenchView.Changes => UIEventBusChangesPage.PAGE_URL,
        EventBusWorkbenchView.TestTools => UIEventBusTestToolsPage.PAGE_URL,
        _ => UIEventBusMonitorPage.PAGE_URL
    };

    private static string SectionIcon(EventBusWorkbenchView section) => section switch
    {
        EventBusWorkbenchView.Providers => MoIcons.Cloud,
        EventBusWorkbenchView.Subscriptions => MoIcons.Route,
        EventBusWorkbenchView.Changes => MoIcons.History,
        EventBusWorkbenchView.TestTools => MoIcons.Flask,
        _ => MoIcons.Network
    };


    private EventBusWorkbenchSnapshot? _modelSnapshot;
    private EventBusCaptureView? _model;
    private EventBusCaptureView? _inspectionModel;
    private SubscriptionChangeViewModel? _inspectionChange;
    private (string Key, string Id)? _resolvedInspection;
    private readonly Stack<string> _ancestorInspections = new();
    private IDialogReference? _testReference;
    private string? _seenTestId;

    internal EventBusRouteQuery Query { get; private set; } = new("");
    internal string CurrentRoute => Route(View);
    internal bool Busy => _busy || _retired;
    internal EventBusWorkbenchState CaptureState => State;
    internal IReadOnlySet<string> RowListenerIds => TestService.GetActiveRuntimeListenerIds();
    internal int ListenerCount => RowListenerIds.Count + (TestService.IsSubscribed ? 1 : 0);
    internal bool IsEmpty => Model is { Snapshot: { Subscriptions.Count: 0, Providers.Count: 0, TopicStatuses.Count: 0 } };

    internal EventBusCaptureView? Model
    {
        get
        {
            var snapshot = State.Snapshot;
            if (!ReferenceEquals(snapshot, _modelSnapshot))
            {
                _modelSnapshot = snapshot;
                _model = snapshot is null ? null : new EventBusCaptureView(snapshot);
            }
            return _model;
        }
    }

    internal (string Key, string Id)? Inspection
    {
        get
        {
            foreach (var key in new[] { "subId", "topicKey", "changeId" })
                if (Query[key] is { Length: > 0 } id) return (key, id);
            return View == EventBusWorkbenchView.Providers && Query["provider"] is { Length: > 0 } provider
                ? ("provider", provider) : null;
        }
    }

    internal EventBusCaptureView? InspectionModel => _inspectionModel;
    internal EventBusTopicRow? SelectedTopic => Inspection is { Key: "topicKey" } route ? _inspectionModel?.Topic(route.Id) : null;
    internal SubscriptionViewModel? SelectedSubscription => _inspectionChange?.Subscription ??
        (Inspection is { Key: "subId" } route ? _inspectionModel?.Snapshot.Subscriptions.FirstOrDefault(row => row.SubscriptionId == route.Id) : null);
    internal EventBusProviderInfo? SelectedProvider => Inspection is { Key: "provider" } route
        ? _inspectionModel?.Snapshot.Providers.FirstOrDefault(provider => provider.UniqueId == route.Id) : null;
    internal SubscriptionChangeViewModel? InspectionChange => _inspectionChange;
    internal DateTimeOffset? InspectionTime => _inspectionChange is { } change
        ? new DateTimeOffset(DateTime.SpecifyKind(change.Timestamp, DateTimeKind.Utc))
        : _inspectionModel?.Snapshot.CapturedAt;
    internal bool HasInspectionAncestor => _ancestorInspections.Count > 0 ||
        Query["subId"] is not null && (Query["topicKey"] is not null || View == EventBusWorkbenchView.Providers && Query["provider"] is not null);

    private (long? Count, string Role) NavigationCount(EventBusWorkbenchView view) => view switch
    {
        EventBusWorkbenchView.Overview when Model is { Attention: > 0 } model => (model.Attention, model.Failed > 0 ? "error" : "warning"),
        EventBusWorkbenchView.Subscriptions when State.Snapshot is { } capture => (capture.Subscriptions.Count, "neutral"),
        EventBusWorkbenchView.Changes when State.ObservedChanges.Count > 0 => (State.ObservedChanges.Count, State.DroppedChangeCount > 0 ? "warning" : "neutral"),
        EventBusWorkbenchView.TestTools when ListenerCount > 0 => (ListenerCount, "info"),
        _ => (null, "neutral")
    };

    protected override void OnInitialized()
    {
        Navigation.LocationChanged += LocationChanged;
        ReadLocation(Navigation.Uri);
    }

    private Task StartObservationAsync() => RunOwned(async () =>
    {
        lock (_ownershipGate)
        {
            if (_retired) return;
            _observation = State.BeginObservation();
            _feed = Monitor.OpenChangeFeed(_token);
            _reader = ReadFeedAsync(_feed);
            _poll = PollAsync();
        }
        await CaptureAsync();
        if (!_retired) StateHasChanged();
    });

    protected override void OnParametersSet()
    {
        if (ReadLocation(Navigation.Uri)) return;
        var legacy = EventBusRouteQuery.To(CurrentRoute, ("event", EventType), ("topic", Topic), ("provider", ProviderId));
        Query = new EventBusRouteQuery(Navigation.ToAbsoluteUri(legacy).Query);
    }

    private void LocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs arguments)
    {
        if (_retired) return;
        _ = InvokeAsync(() =>
        {
            if (_retired || !ReadLocation(arguments.Location)) return;
            StateHasChanged();
        });
    }

    private bool ReadLocation(string location)
    {
        var uri = Navigation.ToAbsoluteUri(location);
        if (!string.Equals(uri.AbsolutePath.TrimEnd('/'), CurrentRoute, StringComparison.OrdinalIgnoreCase)) return false;
        Query = new EventBusRouteQuery(uri.Query);
        if (Inspection is null) _ancestorInspections.Clear();
        UpdateInspection();
        if (Query["testId"] != _seenTestId && _testReference is { } dialog) CancelDialog(dialog);
        return true;
    }

    private void UpdateInspection()
    {
        var selected = Inspection;
        if (_resolvedInspection != selected)
        {
            _inspectionModel = null;
            _inspectionChange = null;
            _resolvedInspection = selected;
        }
        if (selected is not { } value || _inspectionModel is not null || _inspectionChange is not null) return;
        if (value.Key == "changeId")
            _inspectionChange = State.ObservedChanges.FirstOrDefault(change => change.ObservationId == value.Id);
        else
            _inspectionModel = Model;
    }

    private async Task CaptureAsync()
    {
        await State.RefreshAsync(_token);
        if (!_retired) UpdateInspection();
    }

    private Task RefreshAsync() => State.IsRefreshing ? Task.CompletedTask : RunOwned(CaptureAsync);

    private Task AutoRefreshChanged(bool value)
    {
        State.AutoRefresh = value;
        return value ? RefreshAsync() : Task.CompletedTask;
    }

    internal void EditQuery(params (string Key, string? Value)[] edits) =>
        Navigation.NavigateTo(Query.Link(CurrentRoute, edits), replace: true);

    internal void OpenInspection(string key, string id, string? tab = null)
    {
        var selected = Inspection;
        var edits = InspectionEdits();
        if (selected is { } parent && parent != (key, id))
        {
            _ancestorInspections.Push(Navigation.Uri);
            if (key == "subId" && parent.Key is "topicKey" or "provider") edits.Add(parent);
        }
        edits.Add((key, id));
        if (tab is not null) edits.Add(("tab", tab));
        Navigation.NavigateTo(Query.Link(CurrentRoute, edits.ToArray()));
    }

    internal void BackInspection()
    {
        if (_ancestorInspections.TryPop(out var url)) Navigation.NavigateTo(url, replace: true);
        else if (Query["subId"] is not null) EditQuery(("subId", null), ("tab", null));
    }

    private void OnInspectionVisibilityChanged(bool visible)
    {
        if (visible) return;
        _ancestorInspections.Clear();
        EditQuery(InspectionEdits().ToArray());
    }

    private List<(string Key, string? Value)> InspectionEdits()
    {
        List<(string Key, string? Value)> edits = [("topicKey", null), ("subId", null), ("changeId", null), ("tab", null)];
        if (View == EventBusWorkbenchView.Providers) edits.Add(("provider", null));
        return edits;
    }

    internal string InspectionLink(string key, string id, string? tab = null)
    {
        var edits = InspectionEdits();
        edits.Add((key, id));
        if (tab is not null) edits.Add(("tab", tab));
        return Query.Link(CurrentRoute, edits.ToArray());
    }

    internal void OpenTest(string id) => Navigation.NavigateTo(Query.Link(CurrentRoute, ("testId", id)));

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) return StartObservationAsync();

        var id = Query["testId"];
        if (_retired || id == _seenTestId || State.Snapshot is null) return Task.CompletedTask;
        var row = State.Subscriptions.FirstOrDefault(item => item.SubscriptionId == id);
        _seenTestId = id;
        if (id is null) return Task.CompletedTask;
        if (row is null)
        {
            Snackbar.Add(L["Precision:InspNotFoundBody"].Value, Severity.Warning);
            return Task.CompletedTask;
        }
        return RunOwned(async () =>
        {
            await ShowOwnedAsync<SubscriptionTestDialog>(L["SubscriptionTestDialog:Title", row.EventTypeShortName].Value,
                new DialogParameters
                {
                    [nameof(SubscriptionTestDialog.Subscription)] = EventBusWorkbenchState.CloneSubscription(row),
                    [nameof(SubscriptionTestDialog.ProviderKind)] = row.TopicStatus?.Provider ??
                        Model?.Provider(row.Scope, row.ServiceKey)?.ProviderType ?? EventBusProviderKind.Unknown
                },
                onOpened: reference => _testReference = reference);
            _testReference = null;
            if (!_retired && Query["testId"] == id)
            {
                EditQuery(("testId", null));
                await CaptureAsync();
            }
        });
    }

    internal Task CopyAsync(string text) => RunOwned(async () =>
    {
        await JS.InvokeAsync<bool>("MoClipboard.copyText", _token, [text]);
    });

    internal void ListenerChanged() => StateHasChanged();

    private async Task ReadFeedAsync(EventBusMonitorChangeFeed feed)
    {
        try
        {
            while (await feed.Reader.WaitToReadAsync(_token))
            {
                var batch = new List<EventBusMonitorChange>();
                while (feed.Reader.TryRead(out var change)) batch.Add(change);
                if (batch.Count == 0) continue;
                await InvokeAsync(async () =>
                {
                    if (_retired) return;
                    foreach (var change in batch) State.RecordChange(_observation, change, feed.DroppedChangeCount, _token);
                    await CaptureAsync();
                    if (!_retired) StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception) { Logger.LogError(exception, "EventBus workbench observation stopped unexpectedly"); }
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(_token))
            {
                await InvokeAsync(async () =>
                {
                    if (_retired || !State.AutoRefresh || State.IsRefreshing) return;
                    await CaptureAsync();
                    if (!_retired) StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception) { Logger.LogError(exception, "EventBus workbench refresh stopped unexpectedly"); }
    }

    internal Task ActivateAsync(SubscriptionViewModel row) => ExecuteCommandAsync(async () =>
    {
        var result = await Monitor.ActivateSubscriptionAsync(ParseId(row.SubscriptionId));
        Report(result, "Page:Messages:ActivateSucceeded");
    });

    internal Task DeactivateAsync(SubscriptionViewModel row) => ExecuteCommandAsync(async () =>
    {
        var result = await Monitor.DeactivateSubscriptionAsync(ParseId(row.SubscriptionId));
        Report(result, "Page:Messages:DeactivateSucceeded");
    });

    internal Task StopListenerAsync(string id) => ExecuteCommandAsync(async () =>
    {
        var result = await TestService.StopRuntimeListenerAsync(ParseId(id));
        Report(result, "SubscriptionTestDialog:Messages:StopListeningSucceeded");
    });

    internal Task RemoveAsync(SubscriptionViewModel row) => ExecuteCommandAsync(async () =>
    {
        var result = await ShowOwnedAsync<SubscriptionRemoveDialog>(L["Registry:RemoveTitle"].Value,
            new DialogParameters
            {
                [nameof(SubscriptionRemoveDialog.Subscription)] = EventBusWorkbenchState.CloneSubscription(row),
                [nameof(SubscriptionRemoveDialog.ProviderKind)] = row.TopicStatus?.Provider ??
                    Model?.Provider(row.Scope, row.ServiceKey)?.ProviderType ?? EventBusProviderKind.Unknown,
                [nameof(SubscriptionRemoveDialog.HasListener)] = RowListenerIds.Contains(row.SubscriptionId)
            });
        _token.ThrowIfCancellationRequested();
        if (result is not { Canceled: false, Data: true }) return;
        var id = ParseId(row.SubscriptionId);
        if (TestService.GetActiveRuntimeListenerIds().Contains(row.SubscriptionId))
        {
            var stop = await TestService.StopRuntimeListenerAsync(id);
            _token.ThrowIfCancellationRequested();
            var error = stop;
            if (stop.IsFailure()) { Snackbar.Add(error.Message ?? L["Workbench:OperationFailed"].Value, Severity.Error); return; }
        }
        var removal = await Monitor.UnsubscribeAsync(id);
        Report(removal, "Page:Messages:UnsubscribeSucceeded");
    });

    private Task ExecuteCommandAsync(Func<Task> command)
    {
        if (_busy || _retired) return Task.CompletedTask;
        _busy = true;
        return RunOwned(async () =>
        {
            try { _token.ThrowIfCancellationRequested(); await command(); if (!_retired) await CaptureAsync(); }
            finally { _busy = false; if (!_retired) StateHasChanged(); }
        });
    }

    private void Report(Result result, string successKey)
    {
        if (_retired) return;
        var error = result;
        if (result.IsFailure()) Snackbar.Add(error.Message ?? L["Workbench:OperationFailed"].Value, Severity.Error);
        else Snackbar.Add(L[successKey].Value, Severity.Success);
    }

    private static EventSubscriptionId ParseId(string id) => new(Guid.Parse(id));

    private async Task<DialogResult?> ShowOwnedAsync<T>(string title, DialogParameters parameters, Action<IDialogReference>? onOpened = null) where T : IComponent
    {
        _token.ThrowIfCancellationRequested();
        var reference = await DialogService.ShowAsync<T>(title, parameters, new DialogOptions { CloseButton = true, CloseOnEscapeKey = true, MaxWidth = MaxWidth.False });
        lock (_ownershipGate)
        {
            if (_retired) { CancelDialog(reference); return null; }
            _dialogs.Add(reference);
        }
        onOpened?.Invoke(reference);
        try { return await reference.Result; }
        finally { lock (_ownershipGate) { _dialogs.Remove(reference); } }
    }

    private Task RunOwned(Func<Task> action)
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

    private async Task RunAdmissionAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try { _token.ThrowIfCancellationRequested(); await action(); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Logger.LogError(exception, "EventBus workbench operation failed");
            if (!_retired) Snackbar.Add(L["Workbench:OperationFailed"].Value, Severity.Error);
        }
        finally
        {
            lock (_ownershipGate) { _admitted.Remove(completion); }
            completion.TrySetResult();
        }
    }

    /// <summary>Retires observations and dialogs, then drains admitted work before releasing the component token.</summary>
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
        Navigation.LocationChanged -= LocationChanged;
        State.EndObservation(_observation);
        _feed?.Dispose();
        _lifetime.Cancel();
        IDialogReference[] dialogs;
        Task[] pending;
        lock (_ownershipGate) { dialogs = _dialogs.ToArray(); pending = _admitted.Select(admission => admission.Task).ToArray(); }
        foreach (var dialog in dialogs) CancelDialog(dialog);
        await Task.WhenAll(pending.Concat(new[] { _reader, _poll }.OfType<Task>()));
        _lifetime.Dispose();
    }

    private static void CancelDialog(IDialogReference reference)
    {
        var result = DialogResult.Cancel();
        try { reference.Close(result); }
        finally { reference.Dismiss(result); }
    }
}
