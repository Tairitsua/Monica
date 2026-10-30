using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using MudBlazor;

namespace Monica.JobScheduler.UI.UIJobScheduler.Components;

public partial class JobDefinitionCleanupContent
{
    /// <summary>Gets the exact owner filter.</summary>
    [Parameter] public string? Owner { get; set; }
    /// <summary>Gets the name or key search.</summary>
    [Parameter] public string? Search { get; set; }
    /// <summary>Gets the readiness filter.</summary>
    [Parameter] public string? State { get; set; }
    /// <summary>Gets the one-based inventory page.</summary>
    [Parameter] public int? Page { get; set; }
    /// <summary>Gets the route-owned evidence owner.</summary>
    [Parameter] public string? InspectOwner { get; set; }
    /// <summary>Gets the route-owned evidence key.</summary>
    [Parameter] public string? InspectJob { get; set; }

    private JobDefinitionCleanupPageState? _state;
    private JobDefinitionCleanupQuery? _loadedQuery;
    private JobId? _inspectedId;
    private string? _search;
    private bool _disposed;
    private Task? _retirement;
    private SchedulerDialogSession<JobId[]>? _cleanupDialog;
    private SchedulerDialogSession<string>? _ownerDialog;
    private JobDefinitionCleanupQuery Query => new()
    {
        OwnerKey = Owner, SearchText = Search, PageNumber = Math.Max(1, Page ?? 1), PageSize = 20,
        State = Enum.TryParse<JobDefinitionCleanupState>(State, true, out var state)
            && DefinitionCleanupPresentation.States.Contains(state) ? state : null
    };
    private bool IsInspecting => InspectOwner is not null && InspectJob is not null && _state?.IsAuthorized == true;
    private JobDefinitionCleanupItem? Inspected => _state?.Inspection?.Id == _inspectedId ? _state?.Inspection : null;

    protected override async Task OnInitializedAsync()
    {
        _state = Factory.Create();
        _state.Changed += ChangedAsync;
        _cleanupDialog = new(OpenCleanupDialogAsync, CompletedAsync);
        _ownerDialog = new(OpenOwnerDialogAsync, CompletedAsync);
        await _state.CheckAccessAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_state is null || _disposed) return;
        var query = Query;
        if (_loadedQuery != query)
        {
            _loadedQuery = query;
            _search = Search;
            await _state.RefreshAsync(query);
        }
        JobId? id = InspectOwner is not null && InspectJob is not null ? new(InspectOwner, InspectJob) : null;
        if (id == _inspectedId) return;
        _inspectedId = id;
        _state.CloseInspection();
        if (id is { } exact) await _state.InspectAsync(exact);
    }

    private Task ChangedAsync() => _disposed ? Task.CompletedTask : InvokeAsync(StateHasChanged);
    private async Task RefreshAsync()
    {
        if (_state is null || _disposed) return;
        await _state.RefreshAsync(Query);
        if (_inspectedId is { } id) await _state.InspectAsync(id);
    }
    private void ChangeOwner(string? value) => Navigate(new() { ["owner"] = value == string.Empty ? null : value, ["page"] = null });
    private void FilterState(JobDefinitionCleanupState? value) => Navigate(new() { ["state"] = value?.ToString(), ["page"] = null });
    private void ChangePage(int page) => Navigate(new() { ["page"] = page });
    private void SearchAsync() => Navigate(new() { ["q"] = string.IsNullOrWhiteSpace(_search) ? null : _search, ["page"] = null });
    private void SearchKey(KeyboardEventArgs args) { if (args.Key == "Enter") SearchAsync(); }
    private void Inspect(JobId id) => Navigate(new() { ["inspectOwner"] = id.OwnerKey, ["inspectJob"] = id.JobKey });
    private void InspectorVisibilityChanged(bool open)
    {
        if (!open && !_disposed) Navigate(new() { ["inspectOwner"] = null, ["inspectJob"] = null });
    }
    private void Navigate(Dictionary<string, object?> values) => Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(values));
    private void SelectReady() => _state?.SelectReadyPage();
    private void ClearSelection() => _state?.ClearSelection();
    private void Toggle(JobId id, bool selected) => _state?.Toggle(id, selected);
    private string Label(JobDefinitionCleanupState state) => DefinitionCleanupPresentation.Label(L, state);
    private string Reason(JobDefinitionCleanupState state) => DefinitionCleanupPresentation.Reason(L, state);
    private static string Ledger(JobId id) => SchedulerRouteBuilder.Ledger(new()
        { SchedulerScopeKey = string.Empty, OwnerKey = id.OwnerKey, JobKey = id.JobKey });
    private string Timestamp(JobDefinitionCleanupItem item) =>
        (item.Definition?.IsPresent == true ? item.Definition.LastObservedAtUtc : item.Definition?.AbsentSinceUtc) is { } instant
            ? Clock.FormatTimestamp(instant) : L["Cleanup:Unknown"];

    private Task PreviewSelectionAsync() => _state is { CanWrite: true, Busy: false } state && state.Selected.Count > 0
        ? _cleanupDialog!.OpenAsync(state.Selected.ToArray()) : Task.CompletedTask;
    private Task RetireOwnerAsync(string owner) => _state is { CanWrite: true, Busy: false }
        ? _ownerDialog!.OpenAsync(owner) : Task.CompletedTask;

    private Task<IDialogReference> OpenCleanupDialogAsync(JobId[] ids) => Dialogs.ShowAsync<DefinitionCleanupDialog>(L["Cleanup:Preview"],
        new DialogParameters<DefinitionCleanupDialog>
        {
            { dialog => dialog.Ids, ids },
            { dialog => dialog.Preview, (Func<IReadOnlyList<JobId>, bool, Task<JobDefinitionCleanupPlan?>>)_state!.PreviewAsync },
            { dialog => dialog.Apply, (Func<JobDefinitionCleanupPlan, Task<JobDefinitionMaintenanceResult?>>)_state.RemoveAsync }
        }, DecisionOptions);

    private Task<IDialogReference> OpenOwnerDialogAsync(string owner) => Dialogs.ShowAsync<OwnerRetirementDialog>(L["Cleanup:RetireTitle"],
        new DialogParameters<OwnerRetirementDialog>
        {
            { dialog => dialog.Owner, owner },
            { dialog => dialog.Preview, (Func<string, Task<JobOwnerRetirementPreview?>>)_state!.PreviewRetirementAsync },
            { dialog => dialog.Apply, (Func<JobOwnerRetirementPreview, Task<JobDefinitionMaintenanceResult?>>)_state.RetireAsync }
        }, DecisionOptions);

    private async Task CompletedAsync<T>(T request, DialogResult? result)
    {
        if (_disposed || result?.Canceled != false) return;
        InspectorVisibilityChanged(false);
        await RefreshAsync();
    }

    private static DialogOptions DecisionOptions => new()
    { FullWidth = true, MaxWidth = MaxWidth.Medium, CloseButton = false, CloseOnEscapeKey = true, BackdropClick = false };

    /// <summary>Retires both dialog producers and state before draining either side of the route.</summary>
    public ValueTask DisposeAsync()
    {
        if (_retirement is not null) return new(_retirement);
        _disposed = true;
        if (_state is not null) _state.Changed -= ChangedAsync;
        var state = _state?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        var cleanup = _cleanupDialog?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        var owner = _ownerDialog?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        _retirement = Task.WhenAll(state, cleanup, owner);
        return new(_retirement);
    }
}
