using Monica.Core.Results;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using MudBlazor;

namespace Monica.JobScheduler.UI.UIJobScheduler.Executions.State;

/// <summary>Owns one execution ledger's draft filters, admitted query snapshots, mutations, and async lifetime.</summary>
/// <remarks>
/// Each read copies its query intent before awaiting authorization or serialization. Only the newest admitted read
/// can replace rendered evidence. Retirement rejects new work with cancellation, drains actual admitted
/// initialization, reads, and mutations, then releases serialization resources.
/// </remarks>
internal sealed class JobExecutionsPageState : IAsyncDisposable
{
    private readonly JobSchedulerFacade _facade;
    private readonly IJobSchedulerUiAccess _access;
    private readonly TimeProvider _timeProvider;
    private readonly SchedulerTimePresentation _timePresentation;
    private readonly SchedulerOperationLifetime _lifetime = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private IReadOnlyCollection<JobExecutionState> _selectedStates = [];
    private QueryIntent? _loadedIntent;
    private int _loadVersion;
    private int _accessVersion;
    private int _pendingMutations;
    private string? _routeOwner, _routeJob;
    private DateTimeOffset? _routeStart, _routeEnd;

    internal JobExecutionsPageState(
        JobSchedulerFacade facade,
        IJobSchedulerUiAccess access,
        int defaultPageSize,
        TimeProvider timeProvider,
        SchedulerTimePresentation timePresentation)
    {
        _facade = facade;
        _access = access;
        _timeProvider = timeProvider;
        _timePresentation = timePresentation;
        PageSize = Math.Clamp(defaultPageSize, 1, 200);
        PageSizeOptions = [.. new[] { 10, 20, 50, 100, PageSize }.Distinct().Order()];
    }

    /// <summary>Raised outside owner locks when consumers should render the current evidence.</summary>
    public event Func<Task>? StateChanged;

    /// <summary>Gets the successfully accepted bounded execution page, if one exists.</summary>
    public IReadOnlyList<JobExecutionInstance> Executions { get; private set; } = [];

    /// <summary>Gets whether authorization has been evaluated.</summary>
    public bool AccessChecked { get; private set; }

    /// <summary>Gets whether current authorization evidence permits inspecting scheduler data.</summary>
    public bool IsAuthorized { get; private set; }

    /// <summary>Gets whether the newest admitted ledger read is pending or running.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Gets whether admitted cancellation work is pending or running.</summary>
    public bool IsMutating { get; private set; }

    /// <summary>Gets whether the current logical query has a successfully accepted snapshot, including zero rows.</summary>
    public bool HasLoadedSnapshot { get; private set; }

    /// <summary>Gets whether the newest read failed instead of producing a valid empty or populated page.</summary>
    public bool IsQueryFailed { get; private set; }

    /// <summary>Gets the latest read or authorization diagnostic, when the failing provider supplies one.</summary>
    /// <remarks><see cref="IsQueryFailed"/> remains true for a failed read whose result has no diagnostic message.</remarks>
    public string? Error { get; private set; }

    /// <summary>Gets the immutable facade request that produced the accepted snapshot.</summary>
    /// <remarks>
    /// The facade pins the configured scheduler scope. The empty scope placeholder here does not mean an unscoped
    /// read. Failed same-intent refreshes retain these actual UTC bounds and the prior observation time.
    /// </remarks>
    public JobExecutionQuery? LoadedQuery { get; private set; }

    /// <summary>Gets the newest immutable facade request, with rolling UTC bounds captured at admission.</summary>
    /// <remarks>The facade owns and pins scheduler scope; this request never selects another scope.</remarks>
    public JobExecutionQuery? RequestedQuery { get; private set; }

    /// <summary>Gets or sets the unnormalized case-insensitive instance or job-key search draft.</summary>
    public string? SearchText { get; set; }

    /// <summary>Gets the immutable selected state set. An empty set includes every defined execution state.</summary>
    public IReadOnlyCollection<JobExecutionState> SelectedStates => _selectedStates;

    /// <summary>Gets or sets the enqueue-time range draft.</summary>
    public ExecutionTimeRange TimeRange { get; set; }

    /// <summary>Gets or sets the optional custom scheduler-calendar start date.</summary>
    public DateTime? CustomStartDate { get; set; }

    /// <summary>Gets or sets the optional custom scheduler-calendar end date.</summary>
    public DateTime? CustomEndDate { get; set; }

    /// <summary>Gets whether the custom start calendar date is later than its end date.</summary>
    public bool HasInvalidCustomRange => TimeRange == ExecutionTimeRange.Custom
        && CustomStartDate is { } start && CustomEndDate is { } end && start.Date > end.Date;

    /// <summary>Gets the newest admitted backend ordering.</summary>
    public JobExecutionSortField SortField { get; private set; } = JobExecutionSortField.CreatedAtUtc;

    /// <summary>Gets whether the newest admitted ordering is descending.</summary>
    public bool SortDescending { get; private set; } = true;

    /// <summary>Gets the newest admitted one-based page number.</summary>
    public int PageNumber { get; private set; } = 1;

    /// <summary>Gets native table page-size choices, including the configured default.</summary>
    public int[] PageSizeOptions { get; }

    /// <summary>Gets the newest admitted page size, bounded to 1 through 200.</summary>
    public int PageSize { get; private set; }

    /// <summary>Gets the accepted query's count; meaningful only when <see cref="HasLoadedSnapshot"/> is true.</summary>
    public int TotalCount { get; private set; }

    /// <summary>Gets when the accepted read completed, rather than when pending authorization began.</summary>
    public DateTimeOffset? ObservedAtUtc { get; private set; }

    /// <summary>Gets whether retirement has stopped new async work.</summary>
    public bool IsDisposed => _lifetime.IsRetired;
    internal bool HasExactRange => _routeStart is not null || _routeEnd is not null;

    /// <summary>Evaluates access before the native table requests its first page.</summary>
    /// <exception cref="OperationCanceledException">The state retired before actual initialization completed.</exception>
    public Task InitializeAsync() => _lifetime.RunAsync(async token =>
    {
        var version = _loadVersion;
        var entered = false;
        try
        {
            token.ThrowIfCancellationRequested();
            _lifetime.TryApply(() => { if (version == _loadVersion) IsLoading = true; });
            await NotifyChangedAsync();
            await _loadGate.WaitAsync(token);
            entered = true;
            await EnsureAuthorizedAsync(token);
        }
        finally
        {
            if (entered) _loadGate.Release();
            if (_lifetime.TryApply(() => { if (version == _loadVersion) IsLoading = false; }))
                await NotifyChangedAsync();
        }
    });

    /// <summary>Rechecks current access before the caller opens an operator confirmation.</summary>
    /// <remarks>
    /// The confirmed mutation performs its own authorization check. Denial returns false; a provider fault also
    /// returns false, clears inaccessible evidence, and retains its diagnostic in <see cref="Error"/>.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The owning state retired.</exception>
    public Task<bool> ReauthorizeAsync() => _lifetime.RunAsync(async token =>
    {
        var authorization = await EnsureAuthorizedAsync(token);
        await NotifyChangedAsync();
        return authorization.IsAuthorized;
    });

    /// <summary>Loads one native table page from copied filter, calendar, paging, and ordering intent.</summary>
    /// <remarks>
    /// Each caller receives its own result. Only the newest request can publish shared evidence. Changed intent clears
    /// prior rows; failed same-intent refreshes retain prior evidence with its actual bounds and observation time.
    /// Authorization denial or provider faults clear evidence, and an older authorized read cannot restore it.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller canceled or the owning state retired.</exception>
    public Task<TableData<JobExecutionInstance>> LoadTableAsync(TableState tableState, CancellationToken requestCancellation)
    {
        ArgumentNullException.ThrowIfNull(tableState);
        var intent = new QueryIntent(SearchText, Array.AsReadOnly(_selectedStates.Order().ToArray()), TimeRange,
            CustomStartDate?.Date, CustomEndDate?.Date, Math.Max(1, tableState.Page + 1),
            Math.Clamp(tableState.PageSize, 1, 200), ResolveSortField(tableState.SortLabel),
            tableState.SortDirection != SortDirection.Ascending, _routeOwner, _routeJob, _routeStart, _routeEnd);
        return _lifetime.RunAsync(async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, requestCancellation);
            linked.Token.ThrowIfCancellationRequested();
            var version = 0;
            if (!_lifetime.TryApply(() =>
                {
                    version = ++_loadVersion;
                    RequestedQuery = null;
                    PageNumber = intent.PageNumber;
                    PageSize = intent.PageSize;
                    SortField = intent.SortField;
                    SortDescending = intent.SortDescending;
                    if (_loadedIntent is null || !_loadedIntent.Matches(intent)) ClearSnapshot();
                    IsLoading = true;
                    IsQueryFailed = false;
                    Error = null;
                })) throw RetiredCancellation();
            JobExecutionQuery query;
            try { query = CreateQuery(intent, _timeProvider.GetUtcNow()); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _lifetime.TryApply(() =>
                {
                    if (version != _loadVersion) return;
                    IsLoading = false;
                    IsQueryFailed = true;
                    Error = exception.Message;
                });
                await NotifyChangedAsync();
                return EmptyPage();
            }
            if (!_lifetime.TryApply(() => { if (version == _loadVersion) RequestedQuery = query; }))
                throw RetiredCancellation();
            return await LoadAsync(intent, query, version, linked.Token);
        });
    }

    /// <summary>Applies route search and defined states without rewriting the supplied search value.</summary>
    /// <remarks>Search remains the store's case-insensitive identifier search, not an exact-ID query.</remarks>
    /// <exception cref="ObjectDisposedException">This synchronous draft update targets a retired state.</exception>
    public void ApplyInitialQuery(string? states, string? instanceId, string? ownerKey = null, string? jobKey = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? searchText = null, string? range = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _selectedStates = CopyStates(ParseStates(states));
        SearchText = searchText ?? (ownerKey is null && jobKey is null ? instanceId : null);
        TimeRange = range switch { "1h" => ExecutionTimeRange.LastHour, "24h" => ExecutionTimeRange.Last24Hours,
            "7d" => ExecutionTimeRange.Last7Days, "30d" => ExecutionTimeRange.Last30Days, _ => ExecutionTimeRange.All };
        _routeOwner = ownerKey;
        _routeJob = jobKey;
        _routeStart = from;
        _routeEnd = to;
        if (HasExactRange) TimeRange = ExecutionTimeRange.Exact;
    }

    /// <summary>Copies the defined multi-state selection without executing a query.</summary>
    /// <exception cref="ObjectDisposedException">This synchronous draft update targets a retired state.</exception>
    public void SetSelectedStates(IEnumerable<JobExecutionState>? states)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _selectedStates = CopyStates(states ?? []);
    }

    /// <summary>Clears filter drafts while preserving native table ordering.</summary>
    /// <exception cref="ObjectDisposedException">This synchronous draft update targets a retired state.</exception>
    public void ResetFilters()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        SearchText = null;
        _selectedStates = [];
        TimeRange = ExecutionTimeRange.All;
        CustomStartDate = null;
        CustomEndDate = null;
        _routeOwner = _routeJob = null;
        _routeStart = _routeEnd = null;
    }

    /// <summary>Reauthorizes and requests durable cancellation for the exact captured instance identifier.</summary>
    /// <remarks>The caller owns any subsequent ledger reload. Store outcomes are not inferred from UI state.</remarks>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    /// <exception cref="OperationCanceledException">The state retired before the operation completed.</exception>
    public Task<ExecutionCancellationUiResult> RequestCancellationAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return _lifetime.RunAsync<ExecutionCancellationUiResult>(async token =>
        {
            if (!_lifetime.TryApply(() => { _pendingMutations++; IsMutating = true; })) throw RetiredCancellation();
            var entered = false;
            try
            {
                token.ThrowIfCancellationRequested();
                await NotifyChangedAsync();
                await _mutationGate.WaitAsync(token);
                entered = true;
                var authorization = await EnsureAuthorizedAsync(token);
                if (!authorization.IsAuthorized) return new(false, null, authorization.Error);
                var result = await _facade.CancelExecutionAsync(instanceId, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                return result.IsFailed(out var error, out var cancellation)
                    ? new(true, null, error.Message) : new(true, cancellation.Status, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { return new(IsAuthorized, null, exception.Message); }
            finally
            {
                if (entered) _mutationGate.Release();
                if (_lifetime.TryApply(() => { _pendingMutations--; IsMutating = _pendingMutations > 0; }))
                    await NotifyChangedAsync();
            }
        });
    }

    /// <summary>Cancels and drains actual admitted work before releasing serialization resources.</summary>
    /// <remarks>Repeated calls share the same completion, including unexpected faults.</remarks>
    /// <exception cref="AggregateException">Cancellation callbacks, admitted work, or cleanup failed unexpectedly.</exception>
    public ValueTask DisposeAsync()
    {
        StateChanged = null;
        return _lifetime.RetireAsync(() =>
        {
            try { _loadGate.Dispose(); }
            finally { _mutationGate.Dispose(); }
        });
    }

    private async Task<TableData<JobExecutionInstance>> LoadAsync(
        QueryIntent intent, JobExecutionQuery query, int version, CancellationToken token)
    {
        var entered = false;
        int? authorizedVersion = null;
        try
        {
            token.ThrowIfCancellationRequested();
            await NotifyChangedAsync();
            await _loadGate.WaitAsync(token);
            entered = true;
            var authorization = await EnsureAuthorizedAsync(token);
            authorizedVersion = authorization.Version;
            if (!authorization.IsAuthorized)
            {
                _lifetime.TryApply(() => { if (version == _loadVersion) IsQueryFailed = authorization.Error is not null; });
                return EmptyPage();
            }
            if (intent.HasInvalidRange) return EmptyPage();
            var result = await _facade.QueryExecutionsAsync(query, token);
            token.ThrowIfCancellationRequested();
            if (result.IsFailed(out var error, out var page))
            {
                FailQuery(version, authorization.Version, error.Message);
                return RetainedPage(intent, authorization.Version);
            }
            var observedAtUtc = _timeProvider.GetUtcNow();
            var acceptedAccess = false;
            if (!_lifetime.TryApply(() =>
                {
                    acceptedAccess = CanAcceptAccess(authorization.Version);
                    if (!acceptedAccess || version != _loadVersion) return;
                    Executions = page.Items;
                    TotalCount = page.TotalCount;
                    LoadedQuery = query;
                    _loadedIntent = intent;
                    HasLoadedSnapshot = true;
                    IsQueryFailed = false;
                    Error = null;
                    ObservedAtUtc = observedAtUtc;
                })) throw RetiredCancellation();
            return acceptedAccess ? new() { Items = page.Items, TotalItems = page.TotalCount } : EmptyPage();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _lifetime.TryApply(() =>
            {
                if (version != _loadVersion || (authorizedVersion is { } accessVersion && !CanAcceptAccess(accessVersion))) return;
                IsQueryFailed = true;
                Error = exception.Message;
            });
            return RetainedPage(intent, authorizedVersion ?? _accessVersion);
        }
        finally
        {
            if (entered) _loadGate.Release();
            if (_lifetime.TryApply(() => { if (version == _loadVersion) IsLoading = false; }))
                await NotifyChangedAsync();
        }
    }

    private JobExecutionQuery CreateQuery(QueryIntent intent, DateTimeOffset admittedAtUtc)
    {
        (DateTimeOffset? Start, DateTimeOffset? End) range = intent.TimeRange switch
        {
            ExecutionTimeRange.LastHour => (admittedAtUtc.AddHours(-1), null),
            ExecutionTimeRange.Last24Hours => (admittedAtUtc.AddDays(-1), null),
            ExecutionTimeRange.Last7Days => (admittedAtUtc.AddDays(-7), null),
            ExecutionTimeRange.Last30Days => (admittedAtUtc.AddDays(-30), null),
            ExecutionTimeRange.Exact => (intent.ExactStart, intent.ExactEnd?.AddTicks(-1)),
            ExecutionTimeRange.Custom when !intent.HasInvalidRange => (
                intent.StartDate is { } startDate ? _timePresentation.GetStartOfSchedulerCalendarDayUtc(startDate) : null,
                intent.EndDate is { } endDate ? _timePresentation.GetStartOfSchedulerCalendarDayUtc(endDate.AddDays(1)).AddTicks(-1) : null),
            _ => (null, null)
        };
        return new()
        {
            SchedulerScopeKey = string.Empty,
            OwnerKey = intent.OwnerKey,
            JobKey = intent.JobKey,
            SearchText = intent.SearchText,
            States = intent.States.Count == 0 ? null : intent.States,
            CreatedAfterUtc = range.Start,
            CreatedBeforeUtc = range.End,
            PageNumber = intent.PageNumber,
            PageSize = intent.PageSize,
            SortField = intent.SortField,
            SortDescending = intent.SortDescending
        };
    }

    private async Task<(bool IsAuthorized, string? Error, int Version)> EnsureAuthorizedAsync(CancellationToken token)
    {
        var version = 0;
        if (!_lifetime.TryApply(() => version = _accessVersion)) throw RetiredCancellation();
        try
        {
            var authorized = await _access.IsAuthorizedAsync(token);
            token.ThrowIfCancellationRequested();
            var accepted = false;
            if (!_lifetime.TryApply(() =>
                {
                    if (!authorized)
                    {
                        _accessVersion++;
                        IsAuthorized = false;
                        AccessChecked = true;
                        ClearSnapshot();
                        Error = null;
                        return;
                    }
                    if (version != _accessVersion) return;
                    IsAuthorized = true;
                    AccessChecked = true;
                    accepted = true;
                })) throw RetiredCancellation();
            return (accepted, null, version);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            if (!_lifetime.TryApply(() =>
                {
                    _accessVersion++;
                    IsAuthorized = false;
                    AccessChecked = true;
                    ClearSnapshot();
                    Error = exception.Message;
                })) throw RetiredCancellation();
            return (false, exception.Message, version);
        }
    }

    private bool CanAcceptAccess(int version) => IsAuthorized && version == _accessVersion;

    private void FailQuery(int version, int accessVersion, string? error) => _lifetime.TryApply(() =>
    {
        if (version != _loadVersion || !CanAcceptAccess(accessVersion)) return;
        IsQueryFailed = true;
        Error = error;
    });

    private TableData<JobExecutionInstance> RetainedPage(QueryIntent intent, int accessVersion)
    {
        var result = EmptyPage();
        if (!_lifetime.TryApply(() =>
            {
                if (CanAcceptAccess(accessVersion) && HasLoadedSnapshot && _loadedIntent?.Matches(intent) == true)
                    result = new() { Items = Executions, TotalItems = TotalCount };
            })) throw RetiredCancellation();
        return result;
    }

    private void ClearSnapshot()
    {
        Executions = [];
        TotalCount = 0;
        LoadedQuery = null;
        _loadedIntent = null;
        HasLoadedSnapshot = false;
        IsQueryFailed = false;
        ObservedAtUtc = null;
    }

    private async Task NotifyChangedAsync()
    {
        foreach (var handler in _lifetime.SnapshotHandlers(StateChanged)) await handler();
    }

    private static IEnumerable<JobExecutionState> ParseStates(string? values) =>
        (values ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => Enum.TryParse<JobExecutionState>(value, true, out var state) && Enum.IsDefined(state)
            ? (JobExecutionState?)state : null)
        .OfType<JobExecutionState>();

    private static IReadOnlyCollection<JobExecutionState> CopyStates(IEnumerable<JobExecutionState> states) =>
        Array.AsReadOnly(states.Where(state => Enum.IsDefined(state)).Distinct().Order().ToArray());

    private static JobExecutionSortField ResolveSortField(string? label) =>
        Enum.TryParse<JobExecutionSortField>(label, false, out var field) && Enum.IsDefined(field)
            ? field : JobExecutionSortField.CreatedAtUtc;

    private static TableData<JobExecutionInstance> EmptyPage() => new() { Items = [], TotalItems = 0 };
    private static OperationCanceledException RetiredCancellation() => new(new CancellationToken(canceled: true));

    private sealed record QueryIntent(string? SearchText, IReadOnlyCollection<JobExecutionState> States,
        ExecutionTimeRange TimeRange, DateTime? StartDate, DateTime? EndDate, int PageNumber, int PageSize,
        JobExecutionSortField SortField, bool SortDescending, string? OwnerKey, string? JobKey,
        DateTimeOffset? ExactStart, DateTimeOffset? ExactEnd)
    {
        internal bool HasInvalidRange => TimeRange == ExecutionTimeRange.Custom
            && StartDate is { } start && EndDate is { } end && start > end;

        internal bool Matches(QueryIntent other) => SearchText == other.SearchText && States.SequenceEqual(other.States)
            && TimeRange == other.TimeRange && StartDate == other.StartDate && EndDate == other.EndDate
            && PageNumber == other.PageNumber && PageSize == other.PageSize
            && SortField == other.SortField && SortDescending == other.SortDescending
            && OwnerKey == other.OwnerKey && JobKey == other.JobKey && ExactStart == other.ExactStart && ExactEnd == other.ExactEnd;
    }
}
