using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Monica.ServiceDiscovery.Localization;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.UIServiceDiscovery.State;
using Monica.Tool.Extensions;

namespace Monica.ServiceDiscovery.UIServiceDiscovery.Components;

public partial class CurrentInstanceInfo : IAsyncDisposable
{
    [Inject] private CurrentInstanceInfoState State { get; set; } = default!;
    [Inject] private IStringLocalizer<ServiceDiscoveryResource> L { get; set; } = default!;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly CancellationToken _lifetimeToken;
    private PeriodicTimer? _timer;
    private Task? _polling;
    private Task? _disposeTask;
    private bool _retired;
    private bool _refreshing;

    public CurrentInstanceInfo() => _lifetimeToken = _lifetime.Token;
    private CurrentInstanceSnapshot? Snapshot => State.Snapshot;
    private InstanceState? Instance => Snapshot?.CurrentInstance;
    private LeaderState? ClusterLeader => Snapshot?.ClusterLeaderState;

    protected override async Task OnInitializedAsync()
    {
        await RefreshCoreAsync();
        if (!_retired) await RestartPollingAsync();
    }

    private Task RefreshAsync() => RefreshCoreAsync();
    private async Task RefreshCoreAsync(bool showLoading = true)
    {
        if (_retired) return;
        try
        {
            // A tick and manual refresh share one owner. Skip an already-owned refresh.
            if (!await _refreshGate.WaitAsync(0, _lifetimeToken)) return;
            try
            {
                if (_retired) return;
                _refreshing = true;
                if (showLoading) StateHasChanged();
                await State.RefreshAsync(showLoading, _lifetimeToken);
            }
            finally
            {
                _refreshing = false;
                _refreshGate.Release();
            }
            if (!_retired) StateHasChanged();
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
    }

    private async Task AutoRefreshChanged(bool enabled)
    {
        State.AutoRefreshEnabled = enabled;
        await RestartPollingAsync();
    }

    private async Task IntervalChanged(int seconds)
    {
        State.RefreshInterval = Math.Clamp(seconds, 1, 60);
        await RestartPollingAsync();
    }

    private async Task RestartPollingAsync()
    {
        if (_retired) return;
        try
        {
            await _pollGate.WaitAsync(_lifetimeToken);
            try
            {
                StopPolling();
                if (_polling is not null) await _polling;
                if (_retired || !State.AutoRefreshEnabled) return;
                State.RefreshInterval = Math.Clamp(State.RefreshInterval, 1, 60);
                _timer = new PeriodicTimer(TimeSpan.FromSeconds(State.RefreshInterval));
                _polling = PollAsync(_timer);
            }
            finally { _pollGate.Release(); }
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
    }

    private void StopPolling()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async Task PollAsync(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetimeToken))
                await InvokeAsync(() => RefreshCoreAsync(false));
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
    }

    private string Value(string? value) => string.IsNullOrWhiteSpace(value)
        ? L["Shared:Values:NotSpecified"].Value : value;
    private string Time(DateTime? timestamp) => timestamp is null || timestamp == DateTime.MinValue
        ? L["Shared:Values:NotAvailable"].Value
        : UiTime.Format(timestamp.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(timestamp.Value, DateTimeKind.Utc)
            : timestamp.Value);

    private string HeartbeatAge()
    {
        if (Instance is null || State.CapturedAt is null || Instance.LastHeartbeatTime == DateTime.MinValue)
            return L["Shared:Values:NotAvailable"].Value;
        var heartbeat = DateTime.SpecifyKind(Instance.LastHeartbeatTime, DateTimeKind.Utc);
        return L["Workbench:HeartbeatAgeSeconds", Math.Max(0, (State.CapturedAt.Value - heartbeat).TotalSeconds)].Value;
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _retired = true;
        await _lifetime.CancelAsync();
        StopPolling();
        await _pollGate.WaitAsync();
        try { if (_polling is not null) await _polling; }
        finally { _pollGate.Release(); }
        // Drain manual/provider work as well as polling before retiring the cancellation source.
        await _refreshGate.WaitAsync();
        _refreshGate.Release();
        _lifetime.Dispose();
        // Lightweight admission gates remain valid for continuations already waiting on them.
    }
}
