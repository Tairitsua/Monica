using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Monica.Core.Results;
using Monica.Locker.Leases;
using Monica.Locker.Leases.Diagnostics;
using Monica.Locker.UI.State;

namespace Monica.Locker.UI.Components;

public partial class LeaseWorkbench
{
    [Parameter] public string? Authority { get; set; }
    [Parameter] public string? EvidenceState { get; set; }
    [Parameter] public string? Search { get; set; }
    [Parameter] public string? ResourceAuthority { get; set; }
    [Parameter] public string? ResourceName { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly List<Task> _operations = [];
    private Task? _loop, _setup, _retirement;
    private IJSObjectReference? _module, _visibility;
    private DotNetObjectReference<LeaseWorkbench>? _callback;
    private bool _busy, _accessChecked, _retired, _visible = true;
    private (string?, string?) _selection;
    public LeaseWorkbench() => _token = _lifetime.Token;

    protected override Task OnInitializedAsync() => TrackAsync(async token =>
    {
        await State.RefreshAsync(token);
        _accessChecked = true;
    });

    protected override Task OnParametersSetAsync()
    {
        if (_selection == (ResourceAuthority, ResourceName)) return Task.CompletedTask;
        _selection = (ResourceAuthority, ResourceName);
        var selection = _selection;
        return TrackAsync(async token =>
        {
            if (selection != _selection) return;
            if (string.IsNullOrEmpty(selection.Item1) || string.IsNullOrEmpty(selection.Item2)) State.ClearSelection();
            else if (selection.Item2.Length <= 512 && State.Authorities.FirstOrDefault(item => item.Authority.Id == selection.Item1) is { } source)
                await State.SelectAsync(source.Authority.Id, new LeaseResource(source.Authority.Scope, selection.Item2), token);
            else State.ClearSelection();
        });
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) return _setup = InitializeVisibilityAsync();
        return Task.CompletedTask;
    }

    private async Task InitializeVisibilityAsync()
    {
        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Monica.Locker.UI/lease-workbench.js");
            if (_retired) return;
            _callback = DotNetObjectReference.Create(this);
            _visibility = await _module.InvokeAsync<IJSObjectReference>("watchVisibility", _callback);
            if (!_retired) _loop = RefreshLoopAsync();
        }
        catch (JSDisconnectedException) { }
    }

    /// <summary>Receives visibility changes from the owned module; no provider work is admitted by the callback.</summary>
    [JSInvokable]
    public Task VisibilityChanged(bool visible) => InvokeAsync(() =>
    {
        if (_retired) return;
        _visible = visible;
        Wake();
    });

    private void SetInterval(int seconds)
    {
        if (!LeaseWorkbenchState.RefreshIntervals.Contains(seconds)) return;
        State.RefreshSeconds = seconds; Wake();
    }

    private void Wake() { if (!_retired && _wake.CurrentCount == 0) _wake.Release(); }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (!_token.IsCancellationRequested)
            {
                if (!_visible || State.RefreshSeconds == 0) { await _wake.WaitAsync(_token); continue; }
                if (await _wake.WaitAsync(TimeSpan.FromSeconds(State.RefreshSeconds), _token)) continue;
                await InvokeAsync(async () =>
                {
                    if (!_retired && !_busy && _visible && State.RefreshSeconds > 0)
                        await TrackAsync(State.RefreshAsync);
                });
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
    }

    private async Task ManualRefreshAsync()
    {
        if (_busy || _retired) return;
        await TrackAsync(State.RefreshAsync); Wake();
    }

    private Task RefreshResourceAsync() => TrackAsync(async token =>
    {
        if (State.Selection is { } selected) await State.SelectAsync(selected.Authority, selected.Resource, token);
    });

    private async Task<bool> RevokeAsync(LeaseResourceEvidence target)
    {
        var succeeded = false;
        await TrackAsync(async token =>
        {
            var resource = target.Snapshot.Leases.Single().Resource;
            var outcome = await Facade.RevokeAsync(target.Authority.Id, resource, token);
            // A successful mutation remains successful even if reconciliation is canceled by navigation.
            succeeded = outcome.IsOk();
            token.ThrowIfCancellationRequested();
            if (succeeded) await State.RefreshAsync(token);
        });
        return succeeded;
    }

    private Task TrackAsync(Func<CancellationToken, Task> operation)
    {
        if (_retired) return Task.CompletedTask;
        _operations.RemoveAll(task => task.IsCompletedSuccessfully);
        var task = RunAsync(operation);
        _operations.Add(task);
        return task;
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        try
        {
            await _gate.WaitAsync(_token);
            try
            {
                _busy = true; StateHasChanged();
                await operation(_token);
            }
            finally { _gate.Release(); if (!_retired) { _busy = false; StateHasChanged(); } }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
    }

    private void SetAuthority(string id) => SetFilter(("authority", Authority == id ? null : id));
    private void SetFilter((string Key, string? Value) filter) => Navigation.NavigateTo(
        Navigation.GetUriWithQueryParameter(filter.Key, filter.Value), replace: true);
    private void OpenResource(LeaseRow row) => Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
    {
        ["source"] = row.Authority.Id, ["resource"] = row.Record.Resource.Name
    }));
    private void CloseResource() => Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
    {
        ["source"] = null, ["resource"] = null
    }));

    public ValueTask DisposeAsync() => new(_retirement ??= RetireAsync());
    private async Task RetireAsync()
    {
        _retired = true;
        await _lifetime.CancelAsync();
        try
        {
            try
            {
                if (_setup is not null) await _setup;
            }
            finally
            {
                try
                {
                    if (_visibility is not null)
                    {
                        try { await _visibility.InvokeVoidAsync("dispose"); }
                        finally { await _visibility.DisposeAsync(); }
                    }
                }
                finally { if (_module is not null) await _module.DisposeAsync(); }
            }
        }
        catch (JSDisconnectedException) { }
        finally
        {
            _callback?.Dispose();
            try { await Task.WhenAll(_operations.Append(_loop ?? Task.CompletedTask)); }
            finally { _wake.Dispose(); _gate.Dispose(); _lifetime.Dispose(); }
        }
    }
}
