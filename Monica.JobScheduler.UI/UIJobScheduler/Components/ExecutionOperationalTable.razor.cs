using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.JobScheduler.UI.UIJobScheduler.Components;

/// <summary>Presents a server-backed execution ledger with grouped evidence, native pagination, and one native sort selection.</summary>
/// <remarks>
/// The parent owns query intent, authorization, accepted-snapshot evidence, and modal sessions. Server requests,
/// callbacks, and public reload operations are reserved before invocation and drained by shared retirement.
/// Native page navigation starts a query without returning its task; first-page reload reserves and observes
/// the resulting server query rather than starting a duplicate request.
/// Private presentation references preserve native row identity while immutable record facts change during a
/// same-page refresh. Rendered actions capture the displayed record rather than the mutable presentation owner.
/// A cancellation opener remains focusable on its presentation row after the record becomes terminal; its
/// aria-disabled state and handler guard prohibit another cancellation while native dialogs restore focus.
/// </remarks>
public partial class ExecutionOperationalTable : IAsyncDisposable
{
    /// <summary>Gets whether the accepted query uses terminal completion rather than planned or recorded time.</summary>
    [Parameter] public bool ShowCompletedTime { get; set; }

    private string TimeColumn => L[ShowCompletedTime ? "Workspace:CompletedTime" : "Executions:Columns:PlannedOrRecorded"];

    private DateTimeOffset? RowTimestamp(JobExecutionInstance execution) => ShowCompletedTime
        ? execution.CompletedAtUtc : execution.RecurringOccurrenceUtc ?? execution.CreatedAtUtc;

    [Inject] private IStringLocalizer<JobSchedulerResource> L { get; set; } = null!;
    [Inject] private SchedulerTimePresentation TimePresentation { get; set; } = null!;

    /// <summary>Gets or sets the authorized query delegate. Each call receives copied native page and selected sort values.</summary>
    [Parameter, EditorRequired]
    public Func<TableState, CancellationToken, Task<TableData<JobExecutionInstance>>> ServerData { get; set; } = null!;
    /// <summary>Gets or sets the filters rendered within this ledger's painted surface.</summary>
    [Parameter] public RenderFragment? FilterContent { get; set; }
    /// <summary>Gets or sets the total records from the accepted matching query. Unknown totals are suppressed until a snapshot exists.</summary>
    [Parameter] public int TotalCount { get; set; }
    /// <summary>Gets or sets whether the parent holds a successful snapshot for the current logical query, including a genuine empty result.</summary>
    [Parameter] public bool HasLoadedSnapshot { get; set; }
    /// <summary>Gets or sets whether the latest query attempt failed. Retained same-query records remain the parent's explicit stale evidence.</summary>
    [Parameter] public bool IsQueryFailed { get; set; }
    /// <summary>Gets or sets the accepted snapshot's actual observation instant. No current-clock observation is fabricated.</summary>
    [Parameter] public DateTimeOffset? ObservedAtUtc { get; set; }
    /// <summary>Gets or sets the configured initial page size. The default is 20 records.</summary>
    [Parameter] public int PageSize { get; set; } = 20;
    /// <summary>Gets or sets native page-size choices. Defaults are 10, 20, 50, and 100 records.</summary>
    [Parameter] public int[] PageSizeOptions { get; set; } = [10, 20, 50, 100];
    /// <summary>Gets or sets the owner's query-loading state, which disables refresh and sort controls.</summary>
    [Parameter] public bool IsLoading { get; set; }
    /// <summary>Gets or sets the owner's nonmodal command-disable state.</summary>
    /// <remarks>Keep false throughout native modal opening, result observation, and modal mutations so native focus can restore to the opener. The parent rejects duplicate commands through its session owner.</remarks>
    [Parameter] public bool CommandsDisabled { get; set; }
    /// <summary>Receives the exact immutable clicked record for an inspector request. Identifiers are never trimmed or reconstructed.</summary>
    [Parameter] public EventCallback<JobExecutionInstance> InspectRequested { get; set; }
    /// <summary>Receives the exact immutable clicked nonterminal record for a cancellation request. Confirmation and mutation are parent-owned.</summary>
    [Parameter] public EventCallback<JobExecutionInstance> RequestCancellation { get; set; }
    /// <summary>Gets or sets optional successful-empty text. Null uses the localized no-match message.</summary>
    [Parameter] public string? EmptyText { get; set; }

    private readonly SchedulerOperationLifetime _operations = new();
    private readonly object _pageZeroGate = new();
    private readonly HashSet<TaskCompletionSource<Task>> _pageZeroArrivals = [];
    private readonly Dictionary<string, ExecutionPresentationRow> _presentationRows = new(StringComparer.Ordinal);
    private MudTable<ExecutionPresentationRow>? _table;
    private JobExecutionSortField _sortField = JobExecutionSortField.CreatedAtUtc;
    private SortDirection _sortDirection = SortDirection.Descending;

    /// <summary>Reloads the current native page and observes its actual server operation.</summary>
    public Task ReloadAsync() => OwnUiOperationAsync(token => ReloadCoreAsync(resetPage: false, token));

    /// <summary>Reloads the first native page after a filter changes, observing the actual page-zero server query without issuing a duplicate query.</summary>
    /// <remarks>The task observes query completion; native rendering and browser paint may settle afterward.</remarks>
    public Task ReloadFromFirstPageAsync() => OwnUiOperationAsync(token => ReloadCoreAsync(resetPage: true, token));

    private async Task ReloadCoreAsync(bool resetPage, CancellationToken cancellationToken)
    {
        var table = _table;
        if (table is null || _operations.IsRetired) return;
        if (!resetPage || table.CurrentPage == 0)
        {
            await table.ReloadServerData();
            return;
        }

        var arrival = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pageZeroGate) _pageZeroArrivals.Add(arrival);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            table.NavigateTo(0);
            // Cancellation can stop waiting for admission. Once admitted, observe the independently owned producer itself.
            var producer = await arrival.Task.WaitAsync(cancellationToken);
            await producer;
        }
        finally { lock (_pageZeroGate) _pageZeroArrivals.Remove(arrival); }
    }

    private Task<TableData<ExecutionPresentationRow>> LoadServerDataAsync(TableState nativeState, CancellationToken cancellationToken)
    {
        var state = new TableState
        {
            Page = nativeState.Page,
            PageSize = nativeState.PageSize,
            SortLabel = _sortField.ToString(),
            SortDirection = _sortDirection
        };
        var serverData = ServerData;
        TaskCompletionSource<Task>[] arrivals = [];
        if (state.Page == 0)
        {
            lock (_pageZeroGate)
            {
                arrivals = _pageZeroArrivals.ToArray();
                _pageZeroArrivals.Clear();
            }
        }

        var producer = _operations.RunAsync(async lifetimeToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, cancellationToken);
            var result = await serverData(state, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var rows = result.Items?.ToArray() ?? [];
            TableData<ExecutionPresentationRow>? presentation = null;
            if (!_operations.TryApply(() =>
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var page = new ExecutionPresentationRow[rows.Length];
                    var retainedIds = new HashSet<string>(StringComparer.Ordinal);
                    for (var index = 0; index < rows.Length; index++)
                    {
                        var capture = rows[index];
                        retainedIds.Add(capture.InstanceId);
                        if (!_presentationRows.TryGetValue(capture.InstanceId, out var row))
                        {
                            row = new ExecutionPresentationRow(capture);
                            _presentationRows.Add(capture.InstanceId, row);
                        }
                        else row.Capture = capture;
                        page[index] = row;
                    }
                    foreach (var instanceId in _presentationRows.Keys.Where(id => !retainedIds.Contains(id)).ToArray())
                        _presentationRows.Remove(instanceId);
                    presentation = new() { Items = page, TotalItems = result.TotalItems };
                })) throw new OperationCanceledException(lifetimeToken);
            return presentation!;
        });
        // Arrival only carries the actual producer task; it cannot create a second abandoned fault on retirement.
        foreach (var arrival in arrivals) arrival.TrySetResult(producer);
        return producer;
    }

    private Task ChangeSortFieldAsync(JobExecutionSortField field) => OwnUiOperationAsync(async token =>
    {
        if (!Enum.IsDefined(field) || !_operations.TryApply(() => _sortField = field)) return;
        await ReloadCoreAsync(resetPage: true, token);
    });

    private Task ChangeSortDirectionAsync(SortDirection direction) => OwnUiOperationAsync(async token =>
    {
        if (direction is not (SortDirection.Ascending or SortDirection.Descending)
            || !_operations.TryApply(() => _sortDirection = direction)) return;
        await ReloadCoreAsync(resetPage: true, token);
    });

    private Task InspectAsync(JobExecutionInstance execution)
    {
        if (CommandsDisabled) return Task.CompletedTask;
        var callback = InspectRequested;
        return OwnUiOperationAsync(_ => callback.InvokeAsync(execution));
    }

    private Task RequestCancellationAsync(ExecutionPresentationRow row, JobExecutionInstance execution)
    {
        if (CommandsDisabled || execution.IsTerminal || row.Capture.IsTerminal) return Task.CompletedTask;
        var callback = RequestCancellation;
        return OwnUiOperationAsync(_ =>
        {
            if (!_operations.TryApply(() => row.RetainCancellationOpener = true)) return Task.CompletedTask;
            return callback.InvokeAsync(execution);
        });
    }

    private async Task OwnUiOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_operations.IsRetired) return;
        try { await _operations.RunAsync(operation); }
        catch (OperationCanceledException) when (_operations.IsRetired) { }
    }

    private string FormatSortField(JobExecutionSortField field) => L[$"Executions:Sort:{field}"];
    private string FormatSortDirection(SortDirection direction) => L[direction == SortDirection.Ascending ? "Executions:Sort:Ascending" : "Executions:Sort:Descending"];
    private string FormatDuration(JobExecutionInstance execution) => ObservedAtUtc is { } observed
        ? TimePresentation.FormatExecutionDuration(execution, observed)
        : L["Common:NotAvailable"];

    private static string GetStateIcon(JobExecutionState state) => state switch
    {
        JobExecutionState.Queued => MoIcons.Clock,
        JobExecutionState.Running => MoIcons.PlayCircle,
        JobExecutionState.Succeeded => MoIcons.Success,
        JobExecutionState.Failed => MoIcons.Error,
        JobExecutionState.Cancelled => MoIcons.Error,
        JobExecutionState.Skipped => MoIcons.SkipNext,
        _ => MoIcons.Help
    };

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _operations.RetireAsync(() =>
    {
        lock (_pageZeroGate) _pageZeroArrivals.Clear();
        _presentationRows.Clear();
        _table = null;
    });

    // MudTable v9 keys native rows by item equality. Reference identity survives changing immutable record facts.
    private sealed class ExecutionPresentationRow(JobExecutionInstance capture)
    {
        public JobExecutionInstance Capture { get; set; } = capture;
        public bool RetainCancellationOpener { get; set; }
    }
}
