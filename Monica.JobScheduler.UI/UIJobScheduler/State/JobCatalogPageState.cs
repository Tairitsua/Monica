using Microsoft.Extensions.Localization;
using Monica.Core.Results;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using MudBlazor;

namespace Monica.JobScheduler.UI.UIJobScheduler.State;

internal sealed class JobCatalogPageStateFactory(
    JobSchedulerFacade facade,
    IJobSchedulerUiAccess access,
    IStringLocalizer<JobSchedulerResource> localizer)
{
    internal JobCatalogPageState Create(int pageSize) => new(facade, access, localizer, pageSize);
}

/// <summary>
/// Owns one catalog page's bounded operational query, selection, mutations, and asynchronous lifetime.
/// </summary>
internal sealed class JobCatalogPageState : IAsyncDisposable
{
    private readonly JobSchedulerFacade _facade;
    private readonly IJobSchedulerUiAccess _access;
    private readonly IStringLocalizer<JobSchedulerResource> _localizer;
    private readonly SchedulerOperationLifetime _lifetime = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly HashSet<JobId> _selectedJobIds = [];
    private int _loadVersion;

    internal JobCatalogPageState(
        JobSchedulerFacade facade,
        IJobSchedulerUiAccess access,
        IStringLocalizer<JobSchedulerResource> localizer,
        int pageSize)
    {
        _facade = facade;
        _access = access;
        _localizer = localizer;
        PageSize = Math.Max(1, pageSize);
        PageSizeOptions = [.. new[] { 10, 20, 50, 100, PageSize }.Distinct().Order()];
    }

    internal event Func<Task>? Changed;

    internal IReadOnlyList<JobOperationalSummary> Summaries { get; private set; } = [];
    internal IReadOnlyList<JobDefinition> Definitions { get; private set; } = [];
    internal DateTimeOffset? ObservedAtUtc { get; private set; }
    internal IReadOnlyList<KeyValuePair<string, int>> OwnerFacets { get; private set; } = [];
    internal IReadOnlyDictionary<JobType, int> JobTypeCounts { get; private set; } =
        new Dictionary<JobType, int>();
    internal IReadOnlySet<JobId> SelectedJobIds => _selectedJobIds;
    internal IReadOnlyList<JobOperationalSummary> SelectedRecurringSummaries => Summaries
        .Where(summary => summary.Definition.Declaration.JobType == JobType.Recurring
                          && _selectedJobIds.Contains(summary.Definition.Id))
        .ToArray();

    internal string? SearchText { get; set; }
    internal string? OwnerId { get; set; }

    /// <summary>Gets or sets the selected job type. Ordinary entry includes recurring and triggered definitions.</summary>
    internal JobType? SelectedJobType { get; set; }
    internal bool? SelectedDisabledState { get; set; }

    /// <summary>
    /// Gets the optional presence scope applied to every catalog query. Ordinary entry includes both published
    /// and retained unpublished definitions; explicit true or false selects one population.
    /// </summary>
    internal bool? PresentFilter { get; private set; }

    internal bool AccessChecked { get; private set; }
    internal bool IsAuthorized { get; private set; }
    internal bool IsLoading { get; private set; }
    internal bool IsMutating { get; private set; }
    internal string? Error { get; private set; }

    /// <summary>Gets whether owner and type facets have a successful bounded source snapshot.</summary>
    internal bool HasLoadedFacets { get; private set; }

    /// <summary>Gets a facet-specific load failure, retaining any earlier accepted facet evidence.</summary>
    internal string? FacetsError { get; private set; }
    internal int PageNumber { get; private set; } = 1;
    internal int PageSize { get; private set; }
    internal int[] PageSizeOptions { get; }
    internal int TotalCount { get; private set; }
    internal JobDefinitionSortField SortField { get; private set; } = JobDefinitionSortField.JobName;
    internal bool SortDescending { get; private set; }
    internal bool HasSelection => _selectedJobIds.Count > 0;
    internal bool HasManualOnlySuppressedSelection =>
        SelectedRecurringSummaries.Any(JobSchedulerUiPresentation.IsManualOnlySuppressed);
    internal bool IsDisposed => _lifetime.IsRetired;

    internal Task RefreshCompositionAsync() => _lifetime.RunAsync(async token =>
    {
        if ((await EnsureAuthorizedAsync(token)).IsAuthorized) await LoadFacetsAsync(token);
        await NotifyChangedAsync();
    });

    /// <summary>
    /// Applies the page's exact deep-link filters before the first load. Omitted values include every retained
    /// definition and job type. Explicit presence and owner filters narrow that population.
    /// </summary>
    internal void ApplyInitialQuery(bool? present, string? owner)
    {
        if (present is { } value)
        {
            PresentFilter = value;
            SelectedJobType = null;
        }

        if (!string.IsNullOrWhiteSpace(owner))
        {
            OwnerId = owner;
        }
    }

    internal Task InitializeAsync() => _lifetime.RunAsync(InitializeCoreAsync);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (!_lifetime.TryApply(() => IsLoading = true)) return;
            await NotifyChangedAsync();
            if (!(await EnsureAuthorizedAsync(cancellationToken)).IsAuthorized) return;

            await LoadFacetsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _lifetime.TryApply(() =>
            {
                if (!AccessChecked)
                {
                    ClearResults();
                    AccessChecked = true;
                    IsAuthorized = false;
                }
                FacetsError = exception.Message;
                Error = exception.Message;
            });
        }
        finally
        {
            try
            {
                if (_lifetime.TryApply(() => IsLoading = false)) await NotifyChangedAsync();
            }
            finally { _loadGate.Release(); }
        }
    }

    internal void Search()
    {
        _selectedJobIds.Clear();
    }

    internal void Reset()
    {
        SearchText = null;
        OwnerId = null;
        SelectedJobType = null;
        SelectedDisabledState = null;
        _selectedJobIds.Clear();
    }

    /// <summary>Selects one job type, or all types when <paramref name="jobType"/> is null.</summary>
    internal void SelectJobType(JobType? jobType)
    {
        if (SelectedJobType == jobType)
        {
            return;
        }

        SelectedJobType = jobType;
        _selectedJobIds.Clear();
    }

    internal void SelectDisabledState(bool? disabled)
    {
        if (SelectedDisabledState == disabled)
        {
            return;
        }

        SelectedDisabledState = disabled;
        _selectedJobIds.Clear();
    }

    /// <summary>Loads the exact table request captured at admission; each caller receives its own page.</summary>
    internal Task<TableData<JobOperationalSummary>> LoadTableAsync(
        TableState tableState,
        CancellationToken requestCancellation)
    {
        ArgumentNullException.ThrowIfNull(tableState);
        JobDefinitionQuery? query = null;
        var version = 0;
        _lifetime.TryApply(() =>
        {
            PageNumber = tableState.Page + 1;
            PageSize = Math.Clamp(tableState.PageSize, 1, 200);
            SortField = ResolveSortField(tableState.SortLabel);
            SortDescending = tableState.SortDirection == SortDirection.Descending;
            version = ++_loadVersion;
            query = new JobDefinitionQuery
            {
                SearchText = SearchText,
                OwnerKey = OwnerId,
                IsPresent = PresentFilter,
                JobType = SelectedJobType,
                IsDisabled = SelectedDisabledState,
                SortField = SortField,
                SortDescending = SortDescending,
                PageNumber = PageNumber,
                PageSize = PageSize
            };
        });
        return _lifetime.RunAsync(async lifetimeToken =>
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken, requestCancellation);
            var result = await LoadAsync(query!, version, linkedCancellation.Token);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            return result;
        });
    }

    internal void ToggleSelection(JobOperationalSummary summary)
    {
        if (summary.Definition.Declaration.JobType != JobType.Recurring)
        {
            return;
        }

        var jobId = summary.Definition.Id;
        if (!_selectedJobIds.Add(jobId))
        {
            _selectedJobIds.Remove(jobId);
        }
    }

    internal void SelectAllRecurring(bool selected)
    {
        _selectedJobIds.Clear();
        if (selected)
        {
            foreach (var summary in Summaries.Where(static summary =>
                         summary.Definition.Declaration.JobType == JobType.Recurring))
            {
                _selectedJobIds.Add(summary.Definition.Id);
            }
        }
    }

    internal void ApplyPolicy(JobId jobId, JobPolicy policy)
    {
        var items = Summaries.ToArray();
        var index = Array.FindIndex(items, summary =>
            summary.Definition.Id == jobId);
        if (index < 0)
        {
            return;
        }

        var updatedDefinition = items[index].Definition with { Policy = policy };
        var isRecurring = updatedDefinition.Declaration.JobType == JobType.Recurring;
        var suspensionReasons = isRecurring
            ? JobSchedulerUiPresentation.SetOperatorPolicySuspension(
                items[index].SuspensionReasons,
                updatedDefinition.IsDisabled)
            : JobRecurringScheduleSuspensionReason.None;
        items[index] = items[index] with
        {
            Definition = updatedDefinition,
            RecurringScheduleStatus = isRecurring
                ? suspensionReasons != JobRecurringScheduleSuspensionReason.None
                    ? JobRecurringScheduleStatus.Suspended
                    : JobRecurringScheduleStatus.AwaitingSynchronization
                : JobRecurringScheduleStatus.NotRecurring,
            SuspensionReasons = suspensionReasons,
            NextOccurrenceUtc = isRecurring ? null : items[index].NextOccurrenceUtc
        };
        Summaries = items;
        Definitions = Definitions.Select(definition => definition.Id == jobId ? definition with { Policy = policy } : definition).ToArray();
    }

    internal Task<Res<JobPolicy>> SetDisabledAsync(JobOperationalSummary summary, bool disabled) =>
        _lifetime.RunAsync(token => SetDisabledCoreAsync(summary, disabled, token));

    private async Task<Res<JobPolicy>> SetDisabledCoreAsync(
        JobOperationalSummary summary, bool disabled, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_lifetime.TryApply(() => IsMutating = true)) throw new OperationCanceledException(cancellationToken);
            await NotifyChangedAsync();
            var access = await EnsureAuthorizedAsync(cancellationToken);
            if (!access.IsAuthorized)
                return Res.Fail(access.Error ?? _localizer["Access:DeniedDescription"].Value);
            if (disabled && JobSchedulerUiPresentation.IsManualOnlySuppressed(summary))
                return Res.Fail(_localizer["Catalog:Messages:PauseUnavailableManual"]);

            var definition = summary.Definition;
            var result = await _facade.UpdatePolicyAsync(
                definition.OwnerKey,
                definition.Declaration.JobKey,
                new JobPolicyChange
                {
                    Overrides = definition.Policy.Overrides with { DisabledOverride = disabled },
                    ExpectedConcurrencyStamp = definition.Policy.ConcurrencyStamp
                }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.IsFailed(out _, out var policy))
                _lifetime.TryApply(() => ApplyPolicy(definition.Id, policy));
            return result;
        }
        finally
        {
            try
            {
                if (_lifetime.TryApply(() => IsMutating = false)) await NotifyChangedAsync();
            }
            finally { _mutationGate.Release(); }
        }
    }

    internal Task<Res<JobExecutionInstance>> RunRecurringNowAsync(JobOperationalSummary summary) =>
        _lifetime.RunAsync(token => RunRecurringNowCoreAsync(summary, token));

    private async Task<Res<JobExecutionInstance>> RunRecurringNowCoreAsync(
        JobOperationalSummary summary, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_lifetime.TryApply(() => IsMutating = true)) throw new OperationCanceledException(cancellationToken);
            await NotifyChangedAsync();
            var access = await EnsureAuthorizedAsync(cancellationToken);
            if (!access.IsAuthorized)
                return Res.Fail(access.Error ?? _localizer["Access:DeniedDescription"].Value);
            var definition = summary.Definition;
            var result = await _facade.RunRecurringNowAsync(new JobRecurringRunNowRequest
            {
                OwnerKey = definition.OwnerKey,
                JobKey = definition.Declaration.JobKey
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            try
            {
                if (_lifetime.TryApply(() => IsMutating = false)) await NotifyChangedAsync();
            }
            finally { _mutationGate.Release(); }
        }
    }

    internal Task<Res<JobPolicyBatchUpdateResult>> SetSelectedDisabledAsync(bool disabled) =>
        SetSelectedDisabledAsync(disabled, SelectedRecurringSummaries);

    /// <summary>Mutates only the exact identities and policy revisions accepted by the confirmation dialog.</summary>
    /// <remarks>Selection changes after confirmation or during authorization cannot retarget the admitted batch.</remarks>
    internal Task<Res<JobPolicyBatchUpdateResult>> SetSelectedDisabledAsync(
        bool disabled, IReadOnlyList<JobOperationalSummary> confirmedSelection)
    {
        ArgumentNullException.ThrowIfNull(confirmedSelection);
        var selected = confirmedSelection.ToArray();
        return _lifetime.RunAsync(token => SetSelectedDisabledCoreAsync(disabled, selected, token));
    }

    private async Task<Res<JobPolicyBatchUpdateResult>> SetSelectedDisabledCoreAsync(
        bool disabled, IReadOnlyList<JobOperationalSummary> selected, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_lifetime.TryApply(() => IsMutating = true)) throw new OperationCanceledException(cancellationToken);
            await NotifyChangedAsync();
            var access = await EnsureAuthorizedAsync(cancellationToken);
            if (!access.IsAuthorized)
                return Res.Fail(access.Error ?? _localizer["Access:DeniedDescription"].Value);
            if (selected.Count == 0) return _localizer["Catalog:Batch:Empty"].Value;
            if (selected.Any(static summary => summary.Definition.Declaration.JobType != JobType.Recurring))
                throw new ArgumentException("A catalog policy batch accepts only confirmed recurring definitions.", nameof(selected));
            if (disabled && selected.Any(JobSchedulerUiPresentation.IsManualOnlySuppressed))
                return _localizer["Catalog:Batch:PauseUnavailableManual"].Value;

            var result = await _facade.UpdatePoliciesAsync(new JobPolicyBatchUpdateRequest
            {
                Items = selected.Select(summary => new JobPolicyBatchUpdateItem
                {
                    OwnerKey = summary.Definition.OwnerKey,
                    JobKey = summary.Definition.Declaration.JobKey,
                    Overrides = summary.Definition.Policy.Overrides with { DisabledOverride = disabled },
                    ExpectedConcurrencyStamp = summary.Definition.Policy.ConcurrencyStamp
                }).ToArray()
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.IsFailed(out _, out var batch))
            {
                _lifetime.TryApply(() =>
                {
                    foreach (var item in batch.Items.Where(static item => item.IsSucceeded))
                    {
                        var jobId = new JobId(item.OwnerKey, item.JobKey);
                        ApplyPolicy(jobId, item.Policy!);
                        _selectedJobIds.Remove(jobId);
                    }
                });
            }
            return result;
        }
        finally
        {
            try
            {
                if (_lifetime.TryApply(() => IsMutating = false)) await NotifyChangedAsync();
            }
            finally { _mutationGate.Release(); }
        }
    }

    internal Task<bool> ReauthorizeAsync() => _lifetime.RunAsync(async token =>
        (await EnsureAuthorizedAsync(token)).IsAuthorized);

    public ValueTask DisposeAsync()
    {
        Changed = null;
        return _lifetime.RetireAsync(() =>
        {
            _loadGate.Dispose();
            _mutationGate.Dispose();
        });
    }

    private async Task<TableData<JobOperationalSummary>> LoadAsync(
        JobDefinitionQuery query, int version, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_lifetime.TryApply(() => IsLoading = true)) throw new OperationCanceledException(cancellationToken);
            await NotifyChangedAsync();
            if (!(await EnsureAuthorizedAsync(cancellationToken)).IsAuthorized)
            {
                return new() { Items = [], TotalItems = 0 };
            }
            var result = await _facade.QueryOperationalSummariesAsync(query, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsFailed(out var error, out var page))
            {
                _lifetime.TryApply(() =>
                {
                    if (version != _loadVersion) return;
                    Summaries = [];
                    TotalCount = 0;
                    _selectedJobIds.Clear();
                    Error = error.Message;
                });
                return new() { Items = [], TotalItems = 0 };
            }
            _lifetime.TryApply(() =>
            {
                if (version != _loadVersion) return;
                Summaries = page.Items;
                TotalCount = page.TotalCount;
                Error = null;
                RetainVisibleSelection();
            });
            return new() { Items = page.Items, TotalItems = page.TotalCount };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _lifetime.TryApply(() =>
            {
                if (version != _loadVersion) return;
                Summaries = [];
                TotalCount = 0;
                _selectedJobIds.Clear();
                Error = exception.Message;
            });
            return new() { Items = [], TotalItems = 0 };
        }
        finally
        {
            try
            {
                if (_lifetime.TryApply(() => IsLoading = false)) await NotifyChangedAsync();
            }
            finally { _loadGate.Release(); }
        }
    }

    private async Task LoadFacetsAsync(CancellationToken cancellationToken)
    {
        var result = await _facade.GetOverviewAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.IsFailed(out var error, out var overview))
        {
            _lifetime.TryApply(() => FacetsError = error.Message);
            return;
        }

        var scopedDefinitions = overview.Definitions
            .Where(definition => PresentFilter is null || definition.IsPresent == PresentFilter)
            .ToArray();
        var ownerFacets = scopedDefinitions
            .GroupBy(static definition => definition.OwnerKey, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => KeyValuePair.Create(group.Key, group.Count()))
            .ToArray();
        var jobTypeCounts = scopedDefinitions
            .GroupBy(static definition => definition.Declaration.JobType)
            .ToDictionary(static group => group.Key, static group => group.Count());
        _lifetime.TryApply(() =>
        {
            OwnerFacets = ownerFacets;
            Definitions = overview.Definitions;
            ObservedAtUtc = overview.CapturedAtUtc;
            JobTypeCounts = jobTypeCounts;
            HasLoadedFacets = true;
            FacetsError = null;
        });
    }

    private void ClearResults()
    {
        Summaries = [];
        TotalCount = 0;
        PageNumber = 1;
        Error = null;
        _selectedJobIds.Clear();
        OwnerFacets = [];
        Definitions = [];
        ObservedAtUtc = null;
        JobTypeCounts = new Dictionary<JobType, int>();
        HasLoadedFacets = false;
        FacetsError = null;
    }

    private void RetainVisibleSelection()
    {
        var visibleRecurringJobIds = Summaries
            .Where(static summary => summary.Definition.Declaration.JobType == JobType.Recurring)
            .Select(static summary => summary.Definition.Id)
            .ToHashSet();
        _selectedJobIds.IntersectWith(visibleRecurringJobIds);
    }

    private static JobDefinitionSortField ResolveSortField(string? sortLabel) =>
        Enum.TryParse<JobDefinitionSortField>(sortLabel, ignoreCase: false, out var field)
            ? field
            : JobDefinitionSortField.JobName;

    private async Task<(bool IsAuthorized, string? Error)> EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var authorized = await _access.IsAuthorizedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var accepted = _lifetime.TryApply(() =>
            {
                IsAuthorized = authorized;
                AccessChecked = true;
                if (!authorized) ClearResults();
            });
            return (accepted && authorized, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = string.IsNullOrWhiteSpace(exception.Message)
                ? _localizer["Access:DeniedDescription"].Value : exception.Message;
            _lifetime.TryApply(() =>
            {
                ClearResults();
                IsAuthorized = false;
                AccessChecked = true;
                Error = error;
            });
            return (false, error);
        }
    }

    private async Task NotifyChangedAsync()
    {
        var handlers = _lifetime.SnapshotHandlers(Changed);
        foreach (var handler in handlers)
        {
            await handler();
        }
    }
}
