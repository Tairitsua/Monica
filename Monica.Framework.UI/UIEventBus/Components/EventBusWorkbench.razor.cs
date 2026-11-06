using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core.Results;
using Monica.EventBus.Models;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.Pages;
using Monica.Framework.UI.UIEventBus.Dialogs;
using Monica.Framework.UI.UIEventBus.Models;
using Monica.Framework.UI.UIEventBus.State;
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
    private readonly SubscriptionFilter _filter = new();
    private EventBusMonitorChangeFeed? _feed;
    private Task? _reader;
    private Task? _poll;
    private Task? _dispose;
    private long _observation;
    private bool _retired;
    private bool _busy;
    private bool _autoRefresh = true;
    private bool _queryMissingProvider;
    private string? _activeProviderId;
    private bool _parametersSeen;
    private (EventBusWorkbenchView View, string? Event, string? Topic, string? Provider) _lastParameters;

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

    private string Description => View switch
    {
        EventBusWorkbenchView.Providers => L["Workbench:ProvidersDescription"].Value,
        EventBusWorkbenchView.Subscriptions => L["Workbench:SubscriptionsDescription"].Value,
        EventBusWorkbenchView.Changes => L["Workbench:ChangesDescription"].Value,
        EventBusWorkbenchView.TestTools => L["Workbench:TestToolsDescription"].Value,
        _ => L["Workbench:OverviewDescription"].Value
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

    private List<SubscriptionViewModel> FilteredSubscriptions => _queryMissingProvider ? [] :
        State.ApplyFilter(_filter).Where(row => (EventType == null || string.Equals(row.EventType, EventType, StringComparison.Ordinal))
            && (Topic == null || string.Equals(row.TopicName, Topic, StringComparison.Ordinal))).ToList();

    protected override Task OnInitializedAsync()
    {
        return RunOwned(async () =>
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
        });
    }

    protected override void OnParametersSet()
    {
        var parameters = (View, EventType, Topic, ProviderId);
        if (_parametersSeen && _lastParameters == parameters) return;
        _parametersSeen = true;
        _lastParameters = parameters;
        _filter.Clear();
        _queryMissingProvider = false;
        _activeProviderId = ProviderId;
        ReconcileProviderQuery();
    }

    private void ReconcileProviderQuery()
    {
        if (_retired || State.Snapshot == null) return;
        if (_activeProviderId == null) { _filter.SelectedProvider = null; _queryMissingProvider = false; return; }
        _filter.SelectedProvider = State.Providers.FirstOrDefault(provider => string.Equals(provider.UniqueId, _activeProviderId, StringComparison.Ordinal));
        _queryMissingProvider = _filter.SelectedProvider == null;

    }

    private async Task CaptureAsync()
    {
        await State.RefreshAsync(_token);
        if (!_retired) ReconcileProviderQuery();
    }

    private Task RefreshAsync() => State.IsRefreshing ? Task.CompletedTask : RunOwned(CaptureAsync);
    private Task AutoRefreshChanged(bool value) { _autoRefresh = value; return value ? RefreshAsync() : Task.CompletedTask; }
    private void FilterChanged() { }
    private void ProviderSelectionChanged(string? id) { _activeProviderId = id; ReconcileProviderQuery(); }
    private void ClearAllFilters()
    {
        _filter.Clear();
        _activeProviderId = null;
        _queryMissingProvider = false;
        if (EventType != null || Topic != null || ProviderId != null) Navigation.NavigateTo(UIEventBusSubscriptionsPage.PAGE_URL);
    }

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
                    if (_retired || !_autoRefresh || State.IsRefreshing) return;
                    await CaptureAsync();
                    if (!_retired) StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception) { Logger.LogError(exception, "EventBus workbench refresh stopped unexpectedly"); }
    }

    private Task InspectSubscriptionAsync(SubscriptionViewModel row, DateTimeOffset capturedAt) => RunOwned(() => InspectSubscriptionCoreAsync(row, capturedAt));
    private async Task InspectSubscriptionCoreAsync(SubscriptionViewModel row, DateTimeOffset capturedAt)
    {
        await ShowOwnedAsync<SubscriptionDetailDialog>(L["SubscriptionDetail:Title"].Value, new DialogParameters
        {
            [nameof(SubscriptionDetailDialog.Subscription)] = EventBusWorkbenchState.CloneSubscription(row),
            [nameof(SubscriptionDetailDialog.CapturedAt)] = capturedAt
        });
    }

    private Task InspectChangeAsync(SubscriptionChangeViewModel change) => RunOwned(() =>
        InspectSubscriptionCoreAsync(change.Subscription, new DateTimeOffset(DateTime.SpecifyKind(change.Timestamp, DateTimeKind.Utc))));

    private Task InspectTopicAsync(TopicSubscriptionStatus topic, DateTimeOffset capturedAt) => RunOwned(async () =>
    {
        await ShowOwnedAsync<EventBusTopicDetailDialog>(L["Overview:TopicInspector"].Value, new DialogParameters
        {
            [nameof(EventBusTopicDetailDialog.Status)] = topic with { RecentErrors = topic.RecentErrors.ToArray() },
            [nameof(EventBusTopicDetailDialog.CapturedAt)] = capturedAt
        });
    });

    private Task InspectProviderAsync(EventBusProviderInfo provider, EventBusWorkbenchSnapshot capture) => RunOwned(async () =>
    {
        var scope = provider.IsDistributed ? EventSubscriptionScope.Distributed : EventSubscriptionScope.Local;
        var rows = capture.Subscriptions.Where(row => row.Scope == scope && string.Equals(row.ServiceKey, provider.ServiceKey, StringComparison.Ordinal))
            .Select(EventBusWorkbenchState.CloneSubscription).ToList();
        await ShowOwnedAsync<EventBusProviderDetailDialog>(L.GetProviderDisplayName(provider), new DialogParameters
        {
            [nameof(EventBusProviderDetailDialog.Provider)] = provider,
            [nameof(EventBusProviderDetailDialog.Subscriptions)] = rows,
            [nameof(EventBusProviderDetailDialog.CapturedAt)] = capture.CapturedAt
        });
    });

    private Task TestSubscriptionAsync(SubscriptionViewModel row) => RunOwned(async () =>
    {
        await ShowOwnedAsync<SubscriptionTestDialog>(L["SubscriptionTestDialog:Title", row.EventTypeShortName].Value,
            new DialogParameters { [nameof(SubscriptionTestDialog.Subscription)] = EventBusWorkbenchState.CloneSubscription(row) });
        if (!_retired) await CaptureAsync();
    });

    private Task ActivateAsync(SubscriptionViewModel row) => ExecuteCommandAsync(async () =>
    {
        var result = await Monitor.ActivateSubscriptionAsync(ParseId(row.SubscriptionId));
        Report(result, "Page:Messages:ActivateSucceeded");
    });

    private Task DeactivateAsync(SubscriptionViewModel row) => ExecuteCommandAsync(async () =>
    {
        var result = await Monitor.DeactivateSubscriptionAsync(ParseId(row.SubscriptionId));
        Report(result, "Page:Messages:DeactivateSucceeded");
    });

    private Task StopListenerAsync(string id) => ExecuteCommandAsync(async () =>
    {
        var result = await TestService.StopRuntimeListenerAsync(ParseId(id));
        Report(result, "SubscriptionTestDialog:Messages:StopListeningSucceeded");
    });

    private Task RemoveAsync(SubscriptionViewModel row) => ExecuteCommandAsync(async () =>
    {
        var result = await ShowOwnedAsync<SubscriptionRemoveDialog>(L["Registry:RemoveTitle"].Value,
            new DialogParameters { [nameof(SubscriptionRemoveDialog.Subscription)] = EventBusWorkbenchState.CloneSubscription(row) }, MaxWidth.Small);
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

    private async Task<DialogResult?> ShowOwnedAsync<T>(string title, DialogParameters parameters, MaxWidth maxWidth = MaxWidth.Large) where T : IComponent
    {
        _token.ThrowIfCancellationRequested();
        var reference = await DialogService.ShowAsync<T>(title, parameters, new DialogOptions { CloseButton = true, CloseOnEscapeKey = true, FullWidth = true, MaxWidth = maxWidth });
        lock (_ownershipGate)
        {
            if (_retired) { CancelDialog(reference); return null; }
            _dialogs.Add(reference);
        }
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
