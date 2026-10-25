using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Monica.JobScheduler.UI.UIJobScheduler.Components;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using Monica.UI.Shared.Services;
using Test.Monica.JobScheduler.UI.Infrastructure;
using Xunit;

namespace Test.Monica.JobScheduler.UI.Components;

public sealed class OperationalPolicyScheduleEditorTests
{
    private const string MODULE_PATH =
        "./_content/Monica.JobScheduler.UI/js/cron-descriptions.js";

    private static readonly DateTimeOffset OBSERVED_AT =
        new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DescriptionLoad_WhenDraftChangesWhileJavascriptIsPending_ShouldDrainLatestRequest()
    {
        await using var context = new JobSchedulerUiTestContext();
        var module = context.JSInterop.SetupModule(MODULE_PATH);
        module.Setup<CronDescriptionResult[]>(
                "describeCronExpressions",
                invocation => GetExpression(invocation) == "0 */5 * * * *")
            .SetResult([new("policy-draft", "Initial description", null)]);
        var pending = module.Setup<CronDescriptionResult[]>(
            "describeCronExpressions",
            invocation => GetExpression(invocation) == "0 */10 * * * *");
        var latest = module.Setup<CronDescriptionResult[]>(
            "describeCronExpressions",
            invocation => GetExpression(invocation) == "0 */15 * * * *");
        latest.SetResult([new("policy-draft", "Latest description", null)]);
        var draft = CreateDraft();
        var cut = context.Render<TestScheduleEditor>(parameters => parameters
            .Add(component => component.Draft, draft));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Initial description"));

        draft.SetExpression("0 */10 * * * *");
        await cut.InvokeAsync(cut.Instance.NotifyParametersChangedAsync);
        var drain = cut.InvokeAsync(cut.Instance.RunDescriptionPassAsync);
        pending.Invocations.Should().ContainSingle();

        draft.SetExpression("0 */15 * * * *");
        await cut.InvokeAsync(cut.Instance.NotifyParametersChangedAsync);
        pending.SetResult([new("policy-draft", "Stale description", null)]);
        await drain.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        latest.Invocations.Should().ContainSingle();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Latest description");
            cut.Markup.Should().NotContain("Stale description");
        });
    }

    [Fact]
    public async Task PreviewLoop_WhenClockPassesOccurrenceWhileIdle_ShouldAdvanceAndJoinOnDisposal()
    {
        var clock = new ControllableTimeProvider(OBSERVED_AT);
        await using var context = new JobSchedulerUiTestContext(timeProvider: clock);
        // MudBlazor's context-owned popover service already owns a periodic timer.
        var sharedTimerCount = clock.ActiveTimerCount;
        var module = context.JSInterop.SetupModule(MODULE_PATH);
        module.Setup<CronDescriptionResult[]>("describeCronExpressions")
            .SetResult([new("policy-draft", "Every five minutes", null)]);
        var draft = CreateDraft();
        var cut = context.Render<TestScheduleEditor>(parameters => parameters
            .Add(component => component.Draft, draft));
        cut.WaitForAssertion(() =>
        {
            draft.ProposedNextOccurrenceUtc.Should().Be(OBSERVED_AT.AddMinutes(5));
        });

        clock.Advance(TimeSpan.FromMinutes(6));

        cut.WaitForAssertion(() =>
        {
            draft.ProposedNextOccurrenceUtc.Should().Be(OBSERVED_AT.AddMinutes(10));
            draft.ProposedNextOccurrenceUtc.Should().BeAfter(clock.GetUtcNow());
            cut.Find(".schedule-editor__preview-scroll tbody").TextContent.Should()
                .Contain(context.Services.GetRequiredService<MoTimeFormatter>()
                    .FormatWallTime(draft.ToScheduleTime(OBSERVED_AT.AddMinutes(10))));
        });

        await cut.Instance.DisposeAsync();
        cut.Dispose();

        clock.ActiveTimerCount.Should().Be(sharedTimerCount);
    }

    [Fact]
    public async Task SimpleEveryMinute_WhenExpressionModeChanges_ShouldNeverApplyHiddenSeconds()
    {
        await using var context = new JobSchedulerUiTestContext();
        var module = context.JSInterop.SetupModule(MODULE_PATH);
        module.Setup<CronDescriptionResult[]>("describeCronExpressions")
            .SetResult([new("policy-draft", "Description", null)]);
        var draft = CreateDraft();
        var cut = context.Render<TestScheduleEditor>(parameters => parameters
            .Add(component => component.Draft, draft));
        var seconds = cut.Find("input[aria-label='Policy:Schedule:Fields:Second']");
        seconds.Change("7");

        draft.SetMode(CronExpressionMode.FiveFields).IsSuccess.Should().BeTrue();
        cut.Render(parameters => parameters.Add(component => component.Draft, draft));
        cut.FindAll("input[aria-label='Policy:Schedule:Fields:Second']").Should().BeEmpty();

        draft.SetMode(CronExpressionMode.SixFieldsWithSeconds).IsSuccess.Should().BeTrue();
        cut.Render(parameters => parameters.Add(component => component.Draft, draft));

        cut.Find("input[aria-label='Policy:Schedule:Fields:Second']")
            .GetAttribute("value").Should().Be("7");
    }

    private static CronPolicyDraft CreateDraft() => CronPolicyDraft.Create(
        "0 */5 * * * *",
        null,
        "UTC",
        null,
        null,
        false,
        null,
        false,
        null,
        OBSERVED_AT,
        OBSERVED_AT.AddMinutes(5));

    private static string? GetExpression(JSRuntimeInvocation invocation) =>
        invocation.Arguments[0] is IReadOnlyList<CronDescriptionRequest> requests
            ? requests.Single().Expression
            : null;

    private sealed class TestScheduleEditor : OperationalPolicyScheduleEditor
    {
        internal Task NotifyParametersChangedAsync() => base.OnParametersSetAsync();

        internal Task RunDescriptionPassAsync() => base.OnAfterRenderAsync(false);
    }

    private sealed class ControllableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ControllableTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;

        internal int ActiveTimerCount
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count(timer => timer.IsActive);
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ControllableTimer(this, callback, state, dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            ControllableTimer[] dueTimers;
            lock (_gate)
            {
                _utcNow += elapsed;
                dueTimers = _timers.Where(timer => timer.IsDue(_utcNow)).ToArray();
            }

            foreach (var timer in dueTimers)
            {
                timer.Fire();
            }
        }

        private sealed class ControllableTimer : ITimer
        {
            private readonly ControllableTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _period;
            private DateTimeOffset? _dueAtUtc;
            private bool _disposed;

            internal ControllableTimer(
                ControllableTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                Change(dueTime, period);
            }

            internal bool IsActive => !_disposed && _dueAtUtc is not null;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._gate)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    _period = period;
                    _dueAtUtc = dueTime == Timeout.InfiniteTimeSpan
                        ? null
                        : _owner._utcNow + dueTime;
                    return true;
                }
            }

            internal bool IsDue(DateTimeOffset utcNow) => IsActive && _dueAtUtc <= utcNow;

            internal void Fire()
            {
                lock (_owner._gate)
                {
                    if (_disposed || _dueAtUtc is null || _dueAtUtc > _owner._utcNow)
                    {
                        return;
                    }

                    _dueAtUtc = _period == Timeout.InfiniteTimeSpan
                        ? null
                        : _owner._utcNow + _period;
                }

                _callback(_state);
            }

            public void Dispose()
            {
                lock (_owner._gate)
                {
                    _disposed = true;
                    _dueAtUtc = null;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
