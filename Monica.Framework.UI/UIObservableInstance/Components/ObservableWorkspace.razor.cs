using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using Monica.Core.ObservableInstance.Models;
using Monica.Core.Results;
using Monica.Framework.UI.UIObservableInstance.Dialogs;
using Monica.Framework.UI.UIObservableInstance.Models;
using Monica.Framework.UI.UIObservableInstance.Support;
using Monica.UI.Shared.Components.Visual;
using MudBlazor;

namespace Monica.Framework.UI.UIObservableInstance.Components;

public partial class ObservableWorkspace
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _operation;
    private Task? _retirement;
    private Task<IJSObjectReference>? _browser;
    private Task<IJSObjectReference>? _attachment;
    private DotNetObjectReference<ObservableWorkspace>? _receiver;
    private IDialogReference? _confirmation;
    private ElementReference _root;
    private readonly Queue<string> _pendingLocations = new();
    private ObservableViewQuery _query = default!;
    private bool _retired;
    private bool _busy;
    private bool _registryRevealed;

    /// <summary>Gets or sets the shell-selected diagnostic view.</summary>
    [Parameter, EditorRequired] public string View { get; set; } = "Overview";

    private bool IsExceptions => View == "Exceptions";
    private MoVisualRole StatusRole => Workspace.ReadError is not null
        ? Workspace.Evidence is null ? MoVisualRole.Error : MoVisualRole.Warning
        : Workspace.Evidence is null && _busy ? MoVisualRole.Info : MoVisualRole.Neutral;

    private string StatusKey => Workspace.ReadError is not null
        ? Workspace.Evidence is null ? "Design:StatusFailed" : "Design:StatusStale"
        : Workspace.Evidence is null && _busy ? "Design:StatusLoading"
        : Workspace.Evidence?.Trackers.Length == 0 ? "Design:StatusEmpty" : "Design:StatusManual";

    protected override Task OnInitializedAsync()
    {
        _query = new(Navigation);
        Navigation.LocationChanged += LocationChanged;
        return Workspace.Evidence is null ? RefreshAsync() : Task.CompletedTask;
    }

    private void LocationChanged(object? sender, LocationChangedEventArgs args)
    {
        if (_retired || !ObservableViewQuery.OwnsPath(Navigation, args.Location)) return;
        _ = InvokeAsync(() =>
        {
            if (_retired) return;
            if (_pendingLocations.Contains(args.Location, StringComparer.Ordinal))
            {
                while (_pendingLocations.Dequeue() != args.Location) { }
                if (_pendingLocations.Count > 0) return;
            }
            else
            {
                _pendingLocations.Clear();
            }

            _query = new(Navigation);
            if (_query["section"] != "registry") _registryRevealed = false;
            StateHasChanged();
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_retired) return;
        try
        {
            if (firstRender)
            {
                _receiver = DotNetObjectReference.Create(this);
                _attachment = AttachAsync();
                await _attachment;
            }
            if (_retired || IsExceptions || _query["section"] != "registry" || _registryRevealed || Workspace.Evidence is null)
                return;

            _registryRevealed = true;
            var browser = await BrowserAsync();
            if (!_retired && _query["section"] == "registry")
                await browser.InvokeVoidAsync("revealRegistry", _root);
        }
        catch (JSDisconnectedException) { }
    }

    private async Task<IJSObjectReference> AttachAsync()
    {
        var browser = await BrowserAsync();
        return await browser.InvokeAsync<IJSObjectReference>("initialize", _root, _receiver);
    }

    /// <summary>Opens the source and sequence selected by a retention horizon input.</summary>
    [JSInvokable]
    public Task OpenChartEvidence(string id, string? tab, string? sequence) => InvokeAsync(() =>
    {
        if (_retired || IsExceptions) return;
        var seq = long.TryParse(sequence, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : (long?)null;
        OpenInspection(new ObservableInspection(id, tab, seq));
    });

    /// <summary>Applies or clears both retained-exception time bounds in one navigation.</summary>
    [JSInvokable]
    public Task SetChartWindow(string? from, string? to) => InvokeAsync(() =>
    {
        if (!_retired && IsExceptions)
            SetFilters(new Dictionary<string, string?> { ["from"] = from, ["to"] = to });
    });

    private Task<IJSObjectReference> BrowserAsync() => _browser ??= JS.InvokeAsync<IJSObjectReference>(
        "import", "./_content/Monica.Framework.UI/js/observable-instance.js").AsTask();

    private string Link(string path) => ObservableViewQuery.Link(Navigation, path);

    private static MoVisualRole ReviewRole(ObservableEvidence evidence) =>
        evidence.Unhealthy.Length > 0 || evidence.Exceptions.Length > 0 ? MoVisualRole.Error :
        evidence.ReviewCount > 0 ? MoVisualRole.Warning : MoVisualRole.Neutral;

    private void SetFilters(IReadOnlyDictionary<string, string?> filters)
    {
        var values = filters.ToDictionary(item => item.Key, item => (object?)item.Value);
        values["section"] = null;
        Navigate(values, replace: true);
    }

    private void OpenInspection(ObservableInspection inspection) => Navigate(new Dictionary<string, object?>
    {
        ["inspect"] = inspection.Id,
        ["tab"] = inspection.Tab is null || inspection.Tab == "current" && !IsExceptions ? null : inspection.Tab,
        ["seq"] = inspection.Sequence
    }, replace: false);

    private void CloseInspection() => Navigate(new Dictionary<string, object?>
    {
        ["inspect"] = null, ["tab"] = null, ["seq"] = null
    }, replace: false);

    private void Navigate(IReadOnlyDictionary<string, object?> values, bool replace)
    {
        // Interactive navigation acknowledges the URL asynchronously. Compose rapid inputs over the
        // requested URL so a search edit cannot discard a facet selected immediately before it.
        var basis = _pendingLocations.Count == 0 ? Navigation.Uri : _pendingLocations.Last();
        var uri = Navigation.GetUriWithQueryParameters(basis, values);
        if (string.Equals(uri, basis, StringComparison.Ordinal)) return;

        _pendingLocations.Enqueue(uri);
        Navigation.NavigateTo(uri, new NavigationOptions { ReplaceHistoryEntry = replace });
    }

    private Task RefreshAsync() => StartOperation(ReadAsync);

    private Task StartOperation(Func<CancellationToken, Task> action)
    {
        if (_retired || _operation is { IsCompleted: false }) return Task.CompletedTask;
        return _operation = RunAsync(action);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        _busy = true;
        try
        {
            await Task.Yield();
            _lifetime.Token.ThrowIfCancellationRequested();
            await action(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            if (!_retired)
            {
                _busy = false;
                StateHasChanged();
            }
        }
    }

    private async Task ReadAsync(CancellationToken token)
    {
        var result = await MonitorService.CaptureAsync(token);
        if (_retired || token.IsCancellationRequested || !ObservableViewQuery.OwnsPath(Navigation, Navigation.Uri)) return;

        if (result.IsFailure())
        {
            Workspace.ReadError = result.WithoutData().Message;
            return;
        }

        Workspace.Evidence = new ObservableEvidence(result.Data!);
        Workspace.ReadError = null;
    }

    private Task ClearHistoryAsync(ObservableInstanceSnapshot snapshot) => StartOperation(async token =>
    {
        var parameters = new DialogParameters<ObservableClearHistoryDialog> { { dialog => dialog.Instance, snapshot } };
        var dialog = await Dialogs.ShowAsync<ObservableClearHistoryDialog>(L["Design:ClearTitle"], parameters,
            new DialogOptions { CloseOnEscapeKey = true, CloseButton = true, MaxWidth = MaxWidth.Small, FullWidth = true });
        if (_retired)
        {
            dialog.Close();
            return;
        }

        _confirmation = dialog;
        DialogResult? confirmation;
        try { confirmation = await dialog.Result.WaitAsync(token); }
        finally { _confirmation = null; }

        if (confirmation is null || confirmation.Canceled || _retired) return;
        var result = await MonitorService.ClearHistoryAsync(snapshot.Identity, token);
        if (_retired) return;
        if (result.IsFailure())
        {
            Snackbar.Add(L["Design:ClearRejectedToast"], Severity.Warning);
            return;
        }

        Snackbar.Add(L["Design:ClearedToast", snapshot.InstanceName], Severity.Success);
        await ReadAsync(token);
    });

    /// <summary>Retires the route's pending work and its destructive-action confirmation.</summary>
    public ValueTask DisposeAsync() => new(_retirement ??= RetireAsync());

    private async Task RetireAsync()
    {
        _retired = true;
        Navigation.LocationChanged -= LocationChanged;
        await _lifetime.CancelAsync();
        _confirmation?.Close();
        try
        {
            if (_operation is { } operation) await operation;
            if (_attachment is { } attaching)
            {
                var connection = await attaching;
                await connection.InvokeVoidAsync("stop");
                await connection.DisposeAsync();
            }
            if (_browser is { } initialization)
            {
                var browser = await initialization;
                await browser.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { }
        finally
        {
            _receiver?.Dispose();
            _lifetime.Dispose();
        }
    }
}
