using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using Monica.UI.Shared.Services;

namespace Monica.JobScheduler.UI.UIJobScheduler.Components;

/// <summary>
/// Edits a prospective recurring schedule while keeping its deployment-configured timezone immutable.
/// </summary>
public partial class OperationalPolicyScheduleEditor : IAsyncDisposable
{
    private const string DESCRIPTION_KEY = "policy-draft";

    private static readonly TimeSpan MAX_TIMER_DELAY = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private static readonly string[] SyntaxTokens = ["*", ",", "-", "/", "?", "L", "W", "#"];

    private static readonly CronExample[] FiveFieldExamples =
    [
        new("EveryFiveMinutes", "*/5 * * * *"),
        new("EveryDayNoon", "0 12 * * *"),
        new("WeekdaysNine", "0 9 * * MON-FRI"),
        new("MonthlyFirst", "0 0 1 * *")
    ];

    private static readonly CronExample[] SixFieldExamples =
    [
        new("EveryFiveMinutes", "0 */5 * * * *"),
        new("EveryDayNoon", "0 0 12 * * *"),
        new("WeekdaysNine", "0 0 9 * * MON-FRI"),
        new("MonthlyFirst", "0 0 0 1 * *")
    ];

    private readonly SchedulerOperationLifetime _lifetime = new();
    private readonly CronSimpleScheduleSettings _simpleSettings = new();
    private readonly SemaphoreSlim _previewRefreshSignal = new(0, 1);
    private CronDescriptionSession? _descriptionSession;
    private Task? _descriptionTask;
    private Task? _previewTask;
    private string? _descriptionFingerprint;
    private string? _description;
    private string? _conversionFailure;
    private bool _descriptionPending;
    private bool _descriptionRunning;
    private int _descriptionVersion;

    [Inject]
    private IJSRuntime JsRuntime { get; set; } = null!;

    [Inject]
    private IStringLocalizer<JobSchedulerResource> L { get; set; } = null!;

    [Inject]
    private TimeProvider TimeProvider { get; set; } = null!;

    [Inject]
    private MoTimeFormatter UiTime { get; set; } = null!;

    /// <summary>
    /// Gets the component-owned prospective schedule draft.
    /// </summary>
    [Parameter, EditorRequired]
    public CronPolicyDraft Draft { get; set; } = null!;

    /// <summary>
    /// Gets whether policy controls are disabled by an in-progress save.
    /// </summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>
    /// Gets whether the effective operator policy suspends recurring materialization.
    /// </summary>
    [Parameter]
    public bool IsPaused { get; set; }

    /// <summary>
    /// Raised after the prospective schedule changes.
    /// </summary>
    [Parameter]
    public EventCallback Changed { get; set; }

    /// <summary>Gets whether this editor presents the compact Cron-only workflow.</summary>
    [Parameter] public bool Compact { get; set; }

    /// <summary>Gets the accepted expression shown beside the declaration, independently of the current draft.</summary>
    [Parameter] public string? CurrentExpression { get; set; }

    /// <summary>Gets whether the owner still publishes the definition; saving does not republish it.</summary>
    [Parameter] public bool IsPresent { get; set; } = true;

    private static readonly string[] CompactPresets = ["*/5 * * * *", "*/30 * * * *", "0 * * * *", "0 3 * * *", "0 9 * * 1-5", "0 0 1 * *"];
    private string PresetExpression(int index) => (Draft.Mode == CronExpressionMode.SixFieldsWithSeconds ? "0 " : string.Empty) + CompactPresets[index];

    private bool IsDisposed => _lifetime.IsRetired;

    private bool IsDraftValid => Draft.Evaluation.IsValid
                                 && Draft.OverrideExpression is not
                                 {
                                     Length: > JobDeclaration.CRON_EXPRESSION_MAX_LENGTH
                                 };

    private string ValidationError => Draft.OverrideExpression?.Length > JobDeclaration.CRON_EXPRESSION_MAX_LENGTH
        ? L["Policy:Schedule:CronTooLong", JobDeclaration.CRON_EXPRESSION_MAX_LENGTH]
        : Draft.AreBoundariesValid
            ? L["Policy:Schedule:ValidationError"]
            : L["Policy:Schedule:BoundaryValidationError"];

    private IReadOnlyList<CronSyntaxField> SyntaxFields
    {
        get
        {
            CronSyntaxField[] fields =
            [
                new(CronPolicyFieldKind.Minute, "0-59", "* , - /"),
                new(CronPolicyFieldKind.Hour, "0-23", "* , - /"),
                new(CronPolicyFieldKind.DayOfMonth, "1-31", "* , - / ? L W"),
                new(CronPolicyFieldKind.Month, "1-12, JAN-DEC", "* , - /"),
                new(CronPolicyFieldKind.DayOfWeek, "0-7, SUN-SAT", "* , - / ? L #")
            ];
            return Draft.Mode == CronExpressionMode.SixFieldsWithSeconds
                ? [new(CronPolicyFieldKind.Second, "0-59", "* , - /"), .. fields]
                : fields;
        }
    }

    protected override void OnInitialized()
    {
        _descriptionSession = new CronDescriptionSession(JsRuntime);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (IsDisposed) return;
        try
        {
            await _lifetime.RunAsync(_ =>
            {
                RefreshPreview();
                _previewTask ??= _lifetime.RunAsync(RunPreviewLoopAsync);
                QueueDescription();
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (IsDisposed) { }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_descriptionPending || _descriptionRunning || IsDisposed)
        {
            return;
        }

        _descriptionRunning = true;
        var task = _lifetime.RunAsync(DrainDescriptionsAsync);
        _descriptionTask = task;
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (IsDisposed) { }
        finally
        {
            _descriptionRunning = false;
            if (ReferenceEquals(_descriptionTask, task))
            {
                _descriptionTask = null;
            }
        }
    }

    private Task SetExpressionAsync(string? value) => RunDraftChangeAsync(() =>
    {
        Draft.SetExpression(value);
        RefreshPreview();
        _conversionFailure = null;
        QueueDescription();
        return true;
    });

    private Task ResetExpressionAsync() => RunDraftChangeAsync(() =>
    {
        Draft.ResetExpression();
        RefreshPreview();
        _conversionFailure = null;
        QueueDescription();
        return true;
    });

    private Task ChangeModeAsync(CronExpressionMode mode) => RunDraftChangeAsync(() =>
    {
        var conversion = Draft.SetMode(mode);
        _conversionFailure = conversion.Failure switch
        {
            CronExpressionConversionFailure.None => null,
            CronExpressionConversionFailure.NonZeroSeconds => L["Policy:Schedule:NonZeroSecondsConversion"].Value,
            _ => L["Policy:Schedule:InvalidConversion"].Value
        };
        if (!conversion.IsSuccess) return false;
        RefreshPreview();
        QueueDescription();
        return true;
    });

    private Task ApplySimpleSettingsAsync() => SetExpressionAsync(CronPolicyEditor.BuildSimpleExpression(_simpleSettings, Draft.Mode));
    private Task StartBoundaryChangedAsync() => RefreshBoundaryAsync();
    private Task EndBoundaryChangedAsync() => RefreshBoundaryAsync();
    private Task RefreshBoundaryAsync() => RunDraftChangeAsync(() => { RefreshPreview(); return true; });

    private async Task RunDraftChangeAsync(Func<bool> change)
    {
        if (IsDisposed || Disabled) return;
        try
        {
            await _lifetime.RunAsync(async token =>
            {
                if (Disabled || !change()) return;
                token.ThrowIfCancellationRequested();
                await Changed.InvokeAsync();
            });
        }
        catch (OperationCanceledException) when (IsDisposed) { }
    }

    private void QueueDescription()
    {
        var fingerprint = $"{CronDescriptionSession.ResolveCultureName()}\u001f{Draft.EffectiveExpression}";
        if (string.Equals(_descriptionFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _descriptionFingerprint = fingerprint;
        _description = null;
        _descriptionVersion++;
        _descriptionPending = IsDraftValid;
    }

    private async Task DrainDescriptionsAsync(CancellationToken cancellationToken)
    {
        while (_descriptionPending && !IsDisposed)
        {
            _descriptionPending = false;
            await ResolveDescriptionAsync(_descriptionVersion, cancellationToken);
        }
    }

    private async Task ResolveDescriptionAsync(int version, CancellationToken cancellationToken)
    {
        var session = _descriptionSession;
        if (session is null)
        {
            return;
        }

        IReadOnlyList<CronDescriptionResult> results;
        try
        {
            results = await session.DescribeAsync(
                [new CronDescriptionRequest(DESCRIPTION_KEY, Draft.EffectiveExpression)],
                CronDescriptionSession.ResolveCultureName(),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (JSException)
        {
            results = [];
        }

        if (IsDisposed || version != _descriptionVersion)
        {
            return;
        }

        var description = results.FirstOrDefault(result => string.Equals(result.Key, DESCRIPTION_KEY, StringComparison.Ordinal))?.Description;
        _lifetime.TryApply(() => { if (version == _descriptionVersion) _description = description; });
        await InvokeAsync(() => { if (!IsDisposed && version == _descriptionVersion) StateHasChanged(); });
    }

    private void RefreshPreview(bool signalLoop = true)
    {
        Draft.RefreshEvaluation(TimeProvider.GetUtcNow());
        if (signalLoop)
        {
            SignalPreviewLoop();
        }
    }

    private void SignalPreviewLoop()
    {
        if (IsDisposed || _previewRefreshSignal.CurrentCount != 0)
        {
            return;
        }

        try
        {
            _previewRefreshSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A concurrent render already queued the only wake-up the coalescing loop needs.
        }
    }

    private async Task RunPreviewLoopAsync(CancellationToken cancellationToken)
    {
        // A one-shot deadline avoids polling; draft and pause changes interrupt it through the coalesced signal.
        try
        {
            while (!IsDisposed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DateTimeOffset? proposedNextUtc = null;
                await InvokeAsync(() =>
                {
                    if (!IsDisposed) proposedNextUtc = IsPaused ? null : Draft.ProposedNextOccurrenceUtc;
                });
                if (proposedNextUtc is null)
                {
                    await _previewRefreshSignal.WaitAsync(cancellationToken);
                    continue;
                }

                var delay = proposedNextUtc.Value - TimeProvider.GetUtcNow();
                if (delay > TimeSpan.Zero
                    && !await WaitForPreviewDeadlineOrChangeAsync(
                        delay > MAX_TIMER_DELAY ? MAX_TIMER_DELAY : delay,
                        cancellationToken))
                {
                    continue;
                }

                await InvokeAsync(() =>
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    RefreshPreview(signalLoop: false);
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> WaitForPreviewDeadlineOrChangeAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        using var iterationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(delay, TimeProvider, iterationCancellation.Token);
        var changeTask = _previewRefreshSignal.WaitAsync(iterationCancellation.Token);
        var completed = await Task.WhenAny(delayTask, changeTask);
        await iterationCancellation.CancelAsync();
        try
        {
            await Task.WhenAll(delayTask, changeTask);
        }
        catch (OperationCanceledException) when (iterationCancellation.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ReferenceEquals(completed, delayTask);
    }

    private string FieldLabel(CronPolicyFieldKind kind) =>
        L[$"Policy:Schedule:Fields:{kind}"];

    private static string TokenKey(string token) => token switch
    {
        "*" => "Star",
        "," => "Comma",
        "-" => "Hyphen",
        "/" => "Slash",
        "?" => "Question",
        "L" => "Last",
        "W" => "Weekday",
        "#" => "Hash",
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, null)
    };

    private string FormatScheduleTime(DateTimeOffset? value) => value is null
        ? L["Common:NotAvailable"]
        : UiTime.FormatWallTime(Draft.ToScheduleTime(value.Value));

    /// <summary>Cancels and drains actual preview, description and draft callback work before releasing owned resources.</summary>
    /// <remarks>Concurrent disposal shares completion. Each resource cleanup is observed independently after actual work drains.</remarks>
    public ValueTask DisposeAsync() => _lifetime.RetireAsync(CleanupAsync);

    private async ValueTask CleanupAsync()
    {
        var failures = new List<Exception>();
        if (_descriptionSession is { } session)
        {
            try { await session.DisposeAsync(); }
            catch (Exception exception) { failures.Add(exception); }
            _descriptionSession = null;
        }
        try { _previewRefreshSignal.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        if (failures.Count != 0) throw new AggregateException("Schedule editor resource cleanup failed.", failures);
    }

    private sealed record CronSyntaxField(CronPolicyFieldKind Kind, string Range, string Characters);

    private sealed record CronExample(string Key, string Expression);
}
