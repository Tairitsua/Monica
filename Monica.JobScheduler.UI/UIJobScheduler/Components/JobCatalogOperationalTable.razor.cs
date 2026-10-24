using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Executions.State;
using Monica.JobScheduler.UI.UIJobScheduler.Executions.Support;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.JobScheduler.UI.UIJobScheduler.Components;

/// <summary>
/// Presents one server-backed operational catalog page with native sorting, paging, and batched Cron descriptions.
/// </summary>
public partial class JobCatalogOperationalTable : IAsyncDisposable
{
    [Inject]
    private IStringLocalizer<JobSchedulerResource> L { get; set; } = null!;

    [Inject]
    private TimeProvider TimeProvider { get; set; } = null!;

    [Inject]
    private SchedulerTimePresentation TimePresentation { get; set; } = null!;

    [Inject]
    private IJSRuntime JsRuntime { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    [Inject]
    private IDialogService DialogService { get; set; } = null!;

    /// <summary>
    /// Gets the server query used by the native table.
    /// </summary>
    [Parameter, EditorRequired]
    public Func<TableState, CancellationToken, Task<TableData<JobOperationalSummary>>> ServerData { get; set; } = null!;

    /// <summary>
    /// Gets the owner-scoped recurring job identities selected on the visible page.
    /// </summary>
    [Parameter, EditorRequired]
    public IReadOnlySet<JobId> SelectedJobIds { get; set; } = new HashSet<JobId>();

    /// <summary>
    /// Gets the total definitions matching the current filter.
    /// </summary>
    [Parameter]
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets the configured initial table page size.
    /// </summary>
    [Parameter]
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// Gets native table page-size choices.
    /// </summary>
    [Parameter, EditorRequired]
    public int[] PageSizeOptions { get; set; } = [10, 20, 50, 100];

    /// <summary>
    /// Gets whether a catalog mutation is in progress.
    /// </summary>
    [Parameter]
    public bool IsMutating { get; set; }

    /// <summary>
    /// Selects or clears every recurring definition on the visible page.
    /// </summary>
    [Parameter]
    public EventCallback<bool> AllRecurringSelectionChanged { get; set; }

    /// <summary>
    /// Toggles one recurring definition in the batch selection.
    /// </summary>
    [Parameter]
    public EventCallback<JobOperationalSummary> SelectionToggled { get; set; }

    /// <summary>
    /// Pauses or resumes one recurring definition.
    /// </summary>
    [Parameter]
    public EventCallback<JobOperationalSummary> DisabledStateToggled { get; set; }

    /// <summary>
    /// Requests an immediate execution for one definition.
    /// </summary>
    [Parameter]
    public EventCallback<JobOperationalSummary> RunRequested { get; set; }

    /// <summary>Gets or sets the filters rendered inside the registry's painted surface.</summary>
    [Parameter]
    public RenderFragment? FilterContent { get; set; }

    /// <summary>Gets or sets the optional selection toolbar rendered between filters and native rows.</summary>
    [Parameter]
    public RenderFragment? SelectionContent { get; set; }

    /// <summary>Gets or sets whether unavailable query evidence must replace an ordinary empty-result message.</summary>
    [Parameter]
    public bool IsQueryFailed { get; set; }

    /// <summary>Gets or sets the empty-result message. A null value uses the localized no-match message.</summary>
    [Parameter]
    public string? EmptyText { get; set; }

    private readonly Dictionary<string, string?> _cronDescriptions = new(StringComparer.Ordinal);
    private readonly SchedulerOperationLifetime _operations = new();
    private IReadOnlyList<JobOperationalSummary> _visibleSummaries = [];
    private CronDescriptionSession? _cronSession;
    private ExecutionDialogSession? _dialogs;
    private MudTable<JobOperationalSummary>? _table;
    private string? _descriptionFingerprint;
    private bool _descriptionLoadPending;
    private bool _descriptionLoadRunning;
    private int _descriptionVersion;
    private int _disposed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private bool AreAllRecurringSelected
    {
        get
        {
            var recurring = _visibleSummaries.Where(IsRecurring).ToArray();
            return recurring.Length > 0
                   && recurring.All(summary => SelectedJobIds.Contains(summary.Definition.Id));
        }
    }

    protected override void OnInitialized()
    {
        _cronSession = new CronDescriptionSession(JsRuntime);
        _dialogs = new ExecutionDialogSession(
            instanceId => ExecutionDetailDialogLauncher.OpenAsync(DialogService, L, instanceId),
            ReloadAsync);
    }

    protected override void OnParametersSet()
    {
        QueueCronDescriptionLoad(_visibleSummaries);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_descriptionLoadPending || _descriptionLoadRunning || IsDisposed)
        {
            return;
        }

        _descriptionLoadRunning = true;
        try
        {
            await _operations.RunAsync(DrainCronDescriptionsAsync);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
        }
        finally
        {
            _descriptionLoadRunning = false;
        }
    }

    /// <summary>
    /// Reloads the current native table page.
    /// </summary>
    public Task ReloadAsync() =>
        _table is null || IsDisposed ? Task.CompletedTask : _table.ReloadServerData();

    /// <summary>
    /// Reloads from the first native table page after a filter changes.
    /// </summary>
    public Task ReloadFromFirstPageAsync()
    {
        var table = _table;
        if (table is null || IsDisposed)
        {
            return Task.CompletedTask;
        }

        if (table.CurrentPage != 0)
        {
            table.NavigateTo(0);
            return Task.CompletedTask;
        }

        return table.ReloadServerData();
    }

    private async Task<TableData<JobOperationalSummary>> LoadServerDataAsync(
        TableState tableState,
        CancellationToken cancellationToken)
    {
        return await _operations.RunAsync(async lifetimeToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, cancellationToken);
            var tableData = await ServerData(tableState, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (_operations.TryApply(() =>
            {
                _visibleSummaries = tableData.Items?.ToArray() ?? [];
                QueueCronDescriptionLoad(_visibleSummaries);
            }))
            {
                await InvokeAsync(() => { if (!IsDisposed) StateHasChanged(); });
            }
            return tableData;
        });
    }

    private void QueueCronDescriptionLoad(IReadOnlyList<JobOperationalSummary> summaries)
    {
        var cultureName = CronDescriptionSession.ResolveCultureName();
        var fingerprint = string.Join(
            '\u001f',
            summaries
                .Where(IsRecurring)
                .Select(summary => $"{GetCronDescriptionKey(summary)}\u001e{summary.Definition.EffectiveConfiguration.Schedule!.CronExpression}"));
        fingerprint = $"{cultureName}\u001d{fingerprint}";
        if (string.Equals(_descriptionFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _descriptionFingerprint = fingerprint;
        _descriptionLoadPending = true;
        _descriptionVersion++;
        _cronDescriptions.Clear();
    }

    private async Task DrainCronDescriptionsAsync(CancellationToken cancellationToken)
    {
        while (_descriptionLoadPending && !IsDisposed)
        {
            _descriptionLoadPending = false;
            await LoadCronDescriptionsAsync(_descriptionVersion, cancellationToken);
        }
    }

    private async Task LoadCronDescriptionsAsync(int version, CancellationToken cancellationToken)
    {
        var session = _cronSession;
        if (session is null || IsDisposed)
        {
            return;
        }

        var requests = _visibleSummaries
            .Where(IsRecurring)
            .Select(summary => new CronDescriptionRequest(
                GetCronDescriptionKey(summary),
                summary.Definition.EffectiveConfiguration.Schedule!.CronExpression))
            .ToArray();
        if (requests.Length == 0)
        {
            return;
        }

        IReadOnlyList<CronDescriptionResult> results;
        try
        {
            results = await session.DescribeAsync(
                requests,
                CronDescriptionSession.ResolveCultureName(),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (JSException)
        {
            results = requests
                .Select(request => new CronDescriptionResult(request.Key, null, "Unavailable"))
                .ToArray();
        }

        if (IsDisposed || version != _descriptionVersion)
        {
            return;
        }

        foreach (var request in requests)
        {
            _cronDescriptions[request.Key] = null;
        }

        foreach (var result in results)
        {
            _cronDescriptions[result.Key] = string.IsNullOrWhiteSpace(result.Description)
                ? null
                : result.Description;
        }

        await InvokeAsync(() => { if (!IsDisposed) StateHasChanged(); });
    }

    private string GetCronDescription(JobOperationalSummary summary) =>
        _cronDescriptions.TryGetValue(GetCronDescriptionKey(summary), out var description)
            ? description ?? L["Catalog:Cron:Unavailable"]
            : L["Catalog:Cron:Parsing"];

    private Task OpenLatestExecutionAsync(string instanceId) =>
        IsDisposed || _dialogs is null ? Task.CompletedTask : _dialogs.OpenAsync(instanceId);

    private async Task CopyJobKeyAsync(string jobKey)
    {
        if (IsDisposed) return;
        try { await _operations.RunAsync(token => CopyJobKeyCoreAsync(jobKey, token)); }
        catch (OperationCanceledException) when (IsDisposed) { }
    }

    private async Task CopyJobKeyCoreAsync(string jobKey, CancellationToken cancellationToken)
    {
        bool copied;
        try
        {
            copied = await JsRuntime.InvokeAsync<bool>(
                "MoClipboard.copyText",
                cancellationToken,
                jobKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (JSException)
        {
            copied = false;
        }

        if (IsDisposed) return;
        Snackbar.Add(
            copied ? L["Catalog:Actions:CopiedKey"] : L["Catalog:Actions:CopyKeyFailed"],
            copied ? Severity.Success : Severity.Warning);
    }

    private static string GetCronDescriptionKey(JobOperationalSummary summary) =>
        JsonSerializer.Serialize(new[]
        {
            summary.Definition.OwnerKey,
            summary.Definition.Declaration.JobKey,
            summary.Definition.Policy.ConcurrencyStamp
        });

    private string GetNextRunStatusLabel(JobOperationalSummary summary) => summary.RecurringScheduleStatus switch
    {
        JobRecurringScheduleStatus.AwaitingSynchronization => L["Catalog:ScheduleStatus:Awaiting"],
        JobRecurringScheduleStatus.Suspended => GetLifecycleLabel(summary),
        JobRecurringScheduleStatus.Exhausted => L["Catalog:ScheduleStatus:Exhausted"],
        _ => L["Catalog:OnDemand"]
    };

    private string GetNextRunTooltip(DateTimeOffset nextOccurrenceUtc)
    {
        var remaining = nextOccurrenceUtc - TimeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return L["Catalog:NextRun:DueNow"];
        }

        return L["Catalog:NextRun:In", TimePresentation.FormatDuration(remaining)];
    }

    private string GetLifecycleLabel(JobOperationalSummary summary)
    {
        if (!IsRecurring(summary))
        {
            return summary.Definition.IsDisabled ? L["Catalog:Disabled"] : L["Catalog:Enabled"];
        }

        var operatorPaused = IsOperatorPaused(summary);
        var manualSuppressed = IsManualSuppressed(summary);
        return (operatorPaused, manualSuppressed) switch
        {
            (true, true) => L["Catalog:Lifecycle:OperatorAndManual"],
            (true, false) => L["Catalog:Lifecycle:OperatorPaused"],
            (false, true) => L["Catalog:Lifecycle:ManualSuppressed"],
            _ => summary.RecurringScheduleStatus switch
            {
                JobRecurringScheduleStatus.AwaitingSynchronization => L["Catalog:ScheduleStatus:Awaiting"],
                JobRecurringScheduleStatus.Exhausted => L["Catalog:ScheduleStatus:Exhausted"],
                _ => L["Catalog:Lifecycle:Automatic"]
            }
        };
    }

    private string GetLifecycleDescription(JobOperationalSummary summary)
    {
        if (!IsRecurring(summary))
        {
            return summary.Definition.IsDisabled
                ? L["Catalog:Lifecycle:TriggeredDisabledDescription"]
                : L["Catalog:Lifecycle:TriggeredEnabledDescription"];
        }

        var operatorPaused = IsOperatorPaused(summary);
        var manualSuppressed = IsManualSuppressed(summary);
        return (operatorPaused, manualSuppressed) switch
        {
            (true, true) => L["Catalog:Lifecycle:OperatorAndManualDescription"],
            (true, false) => L["Catalog:Lifecycle:OperatorPausedDescription"],
            (false, true) => L["Catalog:Lifecycle:ManualSuppressedDescription"],
            _ => summary.RecurringScheduleStatus switch
            {
                JobRecurringScheduleStatus.AwaitingSynchronization =>
                    L["Catalog:Lifecycle:AwaitingDescription"],
                JobRecurringScheduleStatus.Exhausted => L["Catalog:Lifecycle:ExhaustedDescription"],
                _ => L["Catalog:Lifecycle:AutomaticDescription"]
            }
        };
    }

    private static Color GetLifecycleColor(JobOperationalSummary summary)
    {
        if (!IsRecurring(summary))
        {
            return summary.Definition.IsDisabled ? Color.Warning : Color.Success;
        }

        if (IsOperatorPaused(summary))
        {
            return Color.Warning;
        }

        return IsManualSuppressed(summary)
            ? Color.Info
            : JobSchedulerUiPresentation.GetRecurringScheduleStatusColor(summary.RecurringScheduleStatus);
    }

    private static string GetLifecycleIcon(JobOperationalSummary summary)
    {
        if (!IsRecurring(summary))
        {
            return summary.Definition.IsDisabled
                ? MoIcons.PauseCircle
                : MoIcons.Success;
        }

        if (IsOperatorPaused(summary))
        {
            return MoIcons.PauseCircle;
        }

        if (IsManualSuppressed(summary))
        {
            return MoIcons.Bug;
        }

        return summary.RecurringScheduleStatus switch
        {
            JobRecurringScheduleStatus.AwaitingSynchronization => MoIcons.Refresh,
            JobRecurringScheduleStatus.Exhausted => MoIcons.CalendarX,
            _ => MoIcons.Clock
        };
    }

    private string GetToggleDisabledLabel(JobOperationalSummary summary) =>
        IsOperatorPaused(summary) ? L["Catalog:Actions:Resume"] : L["Catalog:Actions:Pause"];

    private string GetToggleDisabledTooltip(JobOperationalSummary summary) =>
        JobSchedulerUiPresentation.IsManualOnlySuppressed(summary)
            ? L["Catalog:Actions:PauseUnavailableManual"]
            : GetToggleDisabledLabel(summary);

    private static bool IsOperatorPaused(JobOperationalSummary summary) =>
        JobSchedulerUiPresentation.IsOperatorPaused(summary);

    private static bool IsManualSuppressed(JobOperationalSummary summary) =>
        JobSchedulerUiPresentation.IsManualSuppressed(summary);

    private static bool IsRecurring(JobOperationalSummary summary) =>
        summary.Definition.Declaration.JobType == JobType.Recurring;

    private static string GetStateIcon(JobExecutionState state) => state switch
    {
        JobExecutionState.Queued => MoIcons.Clock,
        JobExecutionState.Running => MoIcons.PlayCircle,
        JobExecutionState.Succeeded => MoIcons.Success,
        JobExecutionState.Failed => MoIcons.Error,
        JobExecutionState.Cancelled => MoIcons.Error,
        JobExecutionState.Skipped => MoIcons.SkipNext,
        _ => MoIcons.Circle
    };

    private static string GetDetailHref(JobOperationalSummary summary) =>
        JobSchedulerUiUrls.JobDefinition(summary.Definition.Id);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _descriptionVersion++;
        // Close the native inspector promptly, then drain every admitted native/interop caller.
        var dialogRetirement = _dialogs?.DisposeAsync() ?? ValueTask.CompletedTask;
        return _operations.RetireAsync(async () =>
        {
            var failures = new List<Exception>();
            try { await dialogRetirement; }
            catch (Exception exception) { failures.Add(exception); }
            var session = _cronSession;
            _cronSession = null;
            if (session is not null)
            {
                try { await session.DisposeAsync(); }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (failures.Count > 0) throw new AggregateException("Catalog table retirement failed.", failures);
        });
    }
}
