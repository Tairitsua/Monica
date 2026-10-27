using Monica.Testing.Time;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Models;
using Monica.Core.Modularity.Extensions;
using Monica.Core.ObservableInstance.Services;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services;
using Monica.JobScheduler.Services.Support;
using Monica.Modules;
using NSubstitute;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Services;

public sealed class JobExecutionWorkerHostedServiceTests
{
    private const string OWNER = StoreFixture.OWNER_A;
    private const string WORKER_INSTANCE = "worker-instance-1";
    private static readonly DateTimeOffset NOW = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ClaimAvailableWorkAsync_ShouldClaimAndExecuteOnlyLocalOwnerJobs()
    {
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        var probe = new CancellationProbe();
        var definition = new LocalJobDefinition
        {
            JobClrType = typeof(CancellationObservingRecurringJob),
            Declaration = StoreFixture.RecurringDeclaration(
                typeof(CancellationObservingRecurringJob).FullName!,
                cron: "0 0 0 1 1 *")
        };
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddSingleton(probe);
        hostBuilder.Services.AddScoped<CancellationObservingRecurringJob>();
        hostBuilder.AddMonica(monica => monica.AddExecutionPipeline());
        using var host = hostBuilder.Build();
        var (worker, _) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            options => options.SchedulerScopeKey = StoreFixture.SCOPE);

        await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(
            StoreFixture.SCOPE,
            OWNER,
            [definition.Declaration]), TestContext.Current.CancellationToken);
        await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(
            StoreFixture.SCOPE,
            StoreFixture.OWNER_B,
            [StoreFixture.TriggeredDeclaration("jobs.other")]), TestContext.Current.CancellationToken);
        await store.RunRecurringNowAsync(new JobRecurringRunNowCommand
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = OWNER,
            JobKey = definition.Declaration.JobKey,
            InstanceId = "own-exec"
        }, TestContext.Current.CancellationToken);
        await store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = StoreFixture.OWNER_B,
            JobKey = "jobs.other",
            InstanceId = "other-exec",
            JobArgs = "{\"v\":1}"
        }, TestContext.Current.CancellationToken);

        using var workerStop = new CancellationTokenSource();
        await worker.ClaimAvailableWorkAsync(workerStop.Token);
        var running = await store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            States = [JobExecutionState.Running]
        }, TestContext.Current.CancellationToken);
        running.Items.Should().ContainSingle().Which.InstanceId.Should().Be("own-exec");

        await probe.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await workerStop.CancelAsync();
        // Cooperative job code observes the linked worker token during shutdown.
        await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ClaimAvailableWorkAsync_WhenMultipleOwnersAreLocal_ShouldSendEachOwnersDeclarationManifest()
    {
        using var host = Host.CreateApplicationBuilder().Build();
        var first = CreateDefinition<CancellationObservingRecurringJob>() with { OwnerKey = OWNER };
        var second = CreateDefinition<CancellationIgnoringRecurringJob>() with { OwnerKey = StoreFixture.OWNER_B };
        var store = Substitute.For<IJobSchedulerStore>();
        store.ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JobExecutionLease>>([]));
        var (worker, _) = CreateWorker(
            store,
            [first, second],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            configure: null);
        var firstManifest = new JobOwnerSnapshot(StoreFixture.SCOPE, OWNER, [first.Declaration]).ComputeManifestHash();
        var secondManifest = new JobOwnerSnapshot(StoreFixture.SCOPE, StoreFixture.OWNER_B, [second.Declaration])
            .ComputeManifestHash();

        await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ClaimAsync(
            Arg.Is<JobClaimRequest>(request => request.OwnerKey == OWNER
                                              && request.OwnerManifestHash == firstManifest
                                              && request.JobKeys.Count == 1
                                              && request.JobKeys.Contains(first.Declaration.JobKey)),
            Arg.Any<CancellationToken>());
        await store.Received(1).ClaimAsync(
            Arg.Is<JobClaimRequest>(request => request.OwnerKey == StoreFixture.OWNER_B
                                              && request.OwnerManifestHash == secondManifest
                                              && request.JobKeys.Count == 1
                                              && request.JobKeys.Contains(second.Declaration.JobKey)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLease_WhenLeaseRenewalIsLost_ShouldCancelCooperativeJobWithoutCompletion()
    {
        var probe = new CancellationProbe();
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddSingleton(probe);
        hostBuilder.Services.AddScoped<CancellationObservingRecurringJob>();
        hostBuilder.AddMonica(monica => monica.AddExecutionPipeline());
        using var host = hostBuilder.Build();

        var definition = new LocalJobDefinition
        {
            JobClrType = typeof(CancellationObservingRecurringJob),
            Declaration = StoreFixture.RecurringDeclaration(
                typeof(CancellationObservingRecurringJob).FullName!)
        };
        var store = Substitute.For<IJobSchedulerStore>();
        store.RenewLeaseAsync(
                Arg.Any<JobLeaseKey>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new JobLeaseRenewalResult { Status = JobLeaseRenewalStatus.Lost });
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            configure: null);

        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, options.Value.ExecutionLeaseDuration),
            TestContext.Current.CancellationToken);
        await probe.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await store.Received(1).RenewLeaseAsync(
            Arg.Any<JobLeaseKey>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        await store.DidNotReceive().CompleteAttemptAsync(
            Arg.Any<JobAttemptCompletion>(),
            Arg.Any<CancellationToken>());
        probe.Exited.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteLease_WhenLeaseRenewalThrowsTransiently_ShouldRetryAndCompleteTheAttempt()
    {
        var probe = new CompletionProbe();
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddSingleton(probe);
        hostBuilder.Services.AddScoped<CompletingRecurringJob>();
        hostBuilder.AddMonica(monica => monica.AddExecutionPipeline());
        using var host = hostBuilder.Build();

        var definition = new LocalJobDefinition
        {
            JobClrType = typeof(CompletingRecurringJob),
            Declaration = StoreFixture.RecurringDeclaration(
                typeof(CompletingRecurringJob).FullName!)
        };
        var store = Substitute.For<IJobSchedulerStore>();
        var renewalCount = 0;
        store.RenewLeaseAsync(
                Arg.Any<JobLeaseKey>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // The first renewal fails like an exhausted serialization-retry storm; the retry succeeds and
                // finishes the job inline, proving a transient renewal failure does not cancel cooperative work.
                if (Interlocked.Increment(ref renewalCount) == 1)
                {
                    throw new InvalidOperationException("Simulated transient store failure");
                }

                probe.Completion.TrySetResult();
                return Task.FromResult(CreateActiveRenewal());
            });
        store.CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new JobAttemptCompletionResult
            {
                Status = JobAttemptCompletionStatus.Applied
            }));
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            configure: schedulerOptions => schedulerOptions.ExecutionLeaseRenewInterval = TimeSpan.FromMilliseconds(5));

        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, options.Value.ExecutionLeaseDuration),
            TestContext.Current.CancellationToken);
        await probe.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        renewalCount.Should().BeGreaterThanOrEqualTo(2);
        await store.Received(1).CompleteAttemptAsync(
            Arg.Is<JobAttemptCompletion>(completion => completion.Outcome == JobAttemptOutcome.Succeeded),
            Arg.Any<CancellationToken>());
        await store.DidNotReceive().ReleaseLeaseAsync(
            Arg.Any<JobLeaseKey>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLease_WhenLocalJobIsMissing_ShouldCompleteAttemptAsFailed()
    {
        using var host = Host.CreateApplicationBuilder().Build();
        var store = Substitute.For<IJobSchedulerStore>();
        store.CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new JobAttemptCompletionResult
            {
                Status = JobAttemptCompletionStatus.Applied
            }));
        var (worker, options) = CreateWorker(
            store,
            [],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            configure: null);

        var attempt = worker.ExecuteLeaseAsync(
            CreateLease("jobs.missing", options.Value.ExecutionLeaseDuration),
            TestContext.Current.CancellationToken);
        await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await store.Received(1).CompleteAttemptAsync(
            Arg.Is<JobAttemptCompletion>(completion =>
                completion.Outcome == JobAttemptOutcome.Failed
                && completion.Message!.Contains("Local worker does not contain job 'jobs.missing'", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await store.DidNotReceive().ReleaseLeaseAsync(
            Arg.Any<JobLeaseKey>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLease_WhenTimedOutJobIgnoresCancellation_ShouldFenceLeaseAndRequestHostRestart()
    {
        var probe = new CancellationProbe();
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddSingleton(probe);
        hostBuilder.Services.AddScoped<CancellationIgnoringRecurringJob>();
        hostBuilder.AddMonica(monica => monica.AddExecutionPipeline());
        using var host = hostBuilder.Build();

        var executionTimeout = TimeSpan.FromMilliseconds(30);
        var definition = new LocalJobDefinition
        {
            JobClrType = typeof(CancellationIgnoringRecurringJob),
            Declaration = StoreFixture.RecurringDeclaration(
                typeof(CancellationIgnoringRecurringJob).FullName!)
        };
        var store = Substitute.For<IJobSchedulerStore>();
        store.RenewLeaseAsync(
                Arg.Any<JobLeaseKey>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateActiveRenewal());
        var applicationLifetime = CreateApplicationLifetime(out var restartRequested);
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            configure: schedulerOptions =>
            {
                schedulerOptions.ExecutionCancellationGracePeriod = TimeSpan.FromMilliseconds(30);
                schedulerOptions.ExecutionLeaseRenewInterval = TimeSpan.FromMilliseconds(5);
            },
            applicationLifetime);

        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, executionTimeout),
            TestContext.Current.CancellationToken);
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await restartRequested.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            applicationLifetime.Received(1).StopApplication();
            attempt.IsCompleted.Should().BeFalse();
            await store.DidNotReceive().CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(),
                Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(
                Arg.Any<JobLeaseKey>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            probe.Release.TrySetResult();
            await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteLease_WhenWorkerStopsAndJobIgnoresCancellation_ShouldStopRenewingTheLease()
    {
        var probe = new CancellationProbe();
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddSingleton(probe);
        hostBuilder.Services.AddScoped<CancellationIgnoringRecurringJob>();
        hostBuilder.AddMonica(monica => monica.AddExecutionPipeline());
        using var host = hostBuilder.Build();

        var definition = new LocalJobDefinition
        {
            JobClrType = typeof(CancellationIgnoringRecurringJob),
            Declaration = StoreFixture.RecurringDeclaration(
                typeof(CancellationIgnoringRecurringJob).FullName!)
        };
        var store = Substitute.For<IJobSchedulerStore>();
        // A renewal can run before the job-start continuation requests worker shutdown.
        store.RenewLeaseAsync(
                Arg.Any<JobLeaseKey>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateActiveRenewal());
        var applicationLifetime = CreateApplicationLifetime(out var restartRequested);
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            configure: schedulerOptions =>
            {
                schedulerOptions.ExecutionCancellationGracePeriod = TimeSpan.FromMilliseconds(30);
                schedulerOptions.ExecutionLeaseRenewInterval = TimeSpan.FromMilliseconds(5);
            },
            applicationLifetime);

        using var workerStop = new CancellationTokenSource();
        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, options.Value.ExecutionLeaseDuration),
            workerStop.Token);
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await workerStop.CancelAsync();
            await restartRequested.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            attempt.IsCompleted.Should().BeFalse();
            await store.DidNotReceive().CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(),
                Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(
                Arg.Any<JobLeaseKey>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            probe.Release.TrySetResult();
            await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteLease_WhenShutdownBeginsAfterExecutionCompletes_ShouldCompleteTheAttempt()
    {
        var probe = new CompletionProbe();
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddSingleton(probe);
        hostBuilder.Services.AddScoped<CompletingRecurringJob>();
        hostBuilder.AddMonica(monica => monica.AddExecutionPipeline());
        using var host = hostBuilder.Build();

        var definition = new LocalJobDefinition
        {
            JobClrType = typeof(CompletingRecurringJob),
            Declaration = StoreFixture.RecurringDeclaration(
                typeof(CompletingRecurringJob).FullName!)
        };
        var store = Substitute.For<IJobSchedulerStore>();
        using var workerStop = new CancellationTokenSource();
        store.CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Shutdown starts only once the worker has observed the successful user-code outcome.
                workerStop.Cancel();
                return Task.FromResult(new JobAttemptCompletionResult
                {
                    Status = JobAttemptCompletionStatus.Applied
                });
            });
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            configure: schedulerOptions => schedulerOptions.ExecutionLeaseRenewInterval = TimeSpan.FromSeconds(30));

        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, options.Value.ExecutionLeaseDuration),
            workerStop.Token);
        await probe.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        probe.Completion.TrySetResult();
        await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await store.Received(1).CompleteAttemptAsync(
            Arg.Is<JobAttemptCompletion>(completion => completion.Outcome == JobAttemptOutcome.Succeeded),
            Arg.Any<CancellationToken>());
        await store.DidNotReceive().ReleaseLeaseAsync(
            Arg.Any<JobLeaseKey>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLeaseAsync_WhenRenewalNeverReturns_ShouldCancelAtTheLastConfirmedLeaseDeadline()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CancellationProbe();
        using var host = CreateJobHost<CancellationObservingRecurringJob, CancellationProbe>(probe);
        var definition = CreateDefinition<CancellationObservingRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = new TaskCompletionSource<JobLeaseRenewalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalToken = CancellationToken.None;
        store.RenewLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                renewalToken = call.Arg<CancellationToken>();
                renewalStarted.TrySetResult();
                return renewal.Task;
            });
        var applicationLifetime = CreateApplicationLifetime(out var restartRequested);
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            ConfigureDeadlineOptions,
            applicationLifetime,
            timeProvider: time);
        using var workerStop = new CancellationTokenSource();
        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration),
            workerStop.Token);

        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(7));
            await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(3));
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(33), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(23));

            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            renewalToken.CanBeCanceled.Should().BeTrue();
            renewalToken.IsCancellationRequested.Should().BeTrue();
            probe.Exited.Task.IsCompletedSuccessfully.Should().BeTrue();
            await restartRequested.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
            _ = store.Received(1).RenewLeaseAsync(
                Arg.Any<JobLeaseKey>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(
                Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            renewal.TrySetException(new InvalidOperationException("The stalled renewal failed after retirement."));
            await workerStop.CancelAsync();
            await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteLeaseAsync_WhenRenewalsKeepFailing_ShouldRetryWithoutExtendingTheLeaseDeadline()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CancellationProbe();
        using var host = CreateJobHost<CancellationObservingRecurringJob, CancellationProbe>(probe);
        var definition = CreateDefinition<CancellationObservingRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var renewals = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var renewalCount = 0;
        store.RenewLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var index = Interlocked.Increment(ref renewalCount) - 1;
                if (index < renewals.Length)
                {
                    renewals[index].TrySetResult();
                }

                return Task.FromException<JobLeaseRenewalResult>(
                    new InvalidOperationException("The store is temporarily unavailable."));
            });
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            ConfigureDeadlineOptions,
            timeProvider: time);
        using var workerStop = new CancellationTokenSource();
        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration),
            workerStop.Token);

        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(7));
            await renewals[0].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(14), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(7));
            await renewals[1].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(21), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(7));
            await renewals[2].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(28), TestContext.Current.CancellationToken);

            probe.Exited.Task.IsCompleted.Should().BeFalse();
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(33), TestContext.Current.CancellationToken);
            time.AdvanceAfterPause(TimeSpan.FromSeconds(12));
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            renewalCount.Should().BeGreaterThanOrEqualTo(3);
            probe.Exited.Task.IsCompletedSuccessfully.Should().BeTrue();
            await store.DidNotReceive().CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(
                Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await workerStop.CancelAsync();
            await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteLeaseAsync_WhenRenewalSucceedsAfterCancellation_ShouldKeepTheOriginalStopDeadline()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CancellationProbe();
        using var host = CreateJobHost<CancellationIgnoringRecurringJob, CancellationProbe>(probe);
        var definition = CreateDefinition<CancellationIgnoringRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Inline continuations deliver the late response before the test advances the stopping deadline.
        var renewal = new TaskCompletionSource<JobLeaseRenewalResult>();
        store.RenewLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                renewalStarted.TrySetResult();
                return renewal.Task;
            });
        var applicationLifetime = CreateApplicationLifetime(out var restartRequested);
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            ConfigureDeadlineOptions,
            applicationLifetime,
            time);
        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration),
            TestContext.Current.CancellationToken);

        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(7));
            await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(33), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(26));
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(38), TestContext.Current.CancellationToken);

            renewal.SetResult(CreateActiveRenewal(TimeSpan.FromHours(1)));
            time.Advance(TimeSpan.FromSeconds(5));
            await restartRequested.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            applicationLifetime.Received(1).StopApplication();
            attempt.IsCompleted.Should().BeFalse();
            probe.Exited.Task.IsCompleted.Should().BeFalse();
            _ = store.Received(1).RenewLeaseAsync(
                Arg.Any<JobLeaseKey>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(
                Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            renewal.TrySetResult(CreateActiveRenewal());
            probe.Release.TrySetResult();
            await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteLeaseAsync_WhenCompletionNeverReturns_ShouldCancelTheIndependentFinalizationBudget()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CompletionProbe();
        using var host = CreateJobHost<CompletingRecurringJob, CompletionProbe>(probe);
        var definition = CreateDefinition<CompletingRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var completionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<JobAttemptCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionToken = CancellationToken.None;
        store.CompleteAttemptAsync(Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                completionToken = call.Arg<CancellationToken>();
                completionStarted.TrySetResult();
                return completion.Task;
            });
        var applicationLifetime = CreateApplicationLifetime(out var restartRequested);
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            ConfigureDeadlineOptions,
            applicationLifetime,
            timeProvider: time);
        using var workerStop = new CancellationTokenSource();
        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration),
            workerStop.Token);

        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            probe.Completion.TrySetResult();
            await completionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await workerStop.CancelAsync();
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

            completionToken.CanBeCanceled.Should().BeTrue();
            completionToken.IsCancellationRequested.Should().BeFalse();
            time.Advance(TimeSpan.FromSeconds(3));
            attempt.IsCompleted.Should().BeFalse();
            completionToken.IsCancellationRequested.Should().BeFalse();
            time.Advance(TimeSpan.FromSeconds(1));
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            completionToken.IsCancellationRequested.Should().BeTrue();
            await restartRequested.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
            await store.DidNotReceive().ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>());
            _ = store.Received(1).CompleteAttemptAsync(
                Arg.Is<JobAttemptCompletion>(value => value.Outcome == JobAttemptOutcome.Succeeded),
                Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(
                Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            completion.TrySetException(new InvalidOperationException("Completion failed after its local deadline."));
        }
    }

    [Fact]
    public async Task ExecuteLeaseAsync_WhenShutdownReleaseNeverReturns_ShouldCancelTheIndependentFinalizationBudget()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CancellationProbe();
        using var host = CreateJobHost<CancellationObservingRecurringJob, CancellationProbe>(probe);
        var definition = CreateDefinition<CancellationObservingRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JobAttemptCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseToken = CancellationToken.None;
        store.ReleaseLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                releaseToken = call.Arg<CancellationToken>();
                releaseStarted.TrySetResult();
                return release.Task;
            });
        var applicationLifetime = CreateApplicationLifetime(out var restartRequested);
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            ConfigureDeadlineOptions,
            applicationLifetime,
            timeProvider: time);
        using var workerStop = new CancellationTokenSource();
        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration),
            workerStop.Token);

        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await workerStop.CancelAsync();
            await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

            releaseToken.CanBeCanceled.Should().BeTrue();
            releaseToken.IsCancellationRequested.Should().BeFalse();
            time.Advance(TimeSpan.FromSeconds(4));
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            releaseToken.IsCancellationRequested.Should().BeTrue();
            probe.Exited.Task.IsCompletedSuccessfully.Should().BeTrue();
            await restartRequested.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
            await store.DidNotReceive().ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>());
            _ = store.Received(1).ReleaseLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            release.TrySetException(new InvalidOperationException("Release failed after its local deadline."));
            await workerStop.CancelAsync();
            await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteLeaseAsync_WhenClaimResponseExhaustsTheStoppingRunway_ShouldNotStartUserCode()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CancellationProbe();
        using var host = CreateJobHost<CancellationObservingRecurringJob, CancellationProbe>(probe);
        var definition = CreateDefinition<CancellationObservingRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        store.ReleaseLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>())
            .Returns(new JobAttemptCompletionResult { Status = JobAttemptCompletionStatus.Applied });
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            ConfigureDeadlineOptions,
            timeProvider: time);
        var requestStarted = time.GetTimestamp();
        time.Advance(TimeSpan.FromSeconds(34));

        await worker.ExecuteLeaseAsync(
                CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration),
                TestContext.Current.CancellationToken,
                requestStarted)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        probe.Started.Task.IsCompleted.Should().BeFalse();
        await store.DidNotReceive().RenewLeaseAsync(
            Arg.Any<JobLeaseKey>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().CompleteAttemptAsync(
            Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClaimAvailableWorkAsync_WhenClaimResponseIsDelayed_ShouldCountTheStoreWaitAgainstTheLease()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CancellationProbe();
        using var host = CreateJobHost<CancellationObservingRecurringJob, CancellationProbe>(probe);
        var definition = CreateDefinition<CancellationObservingRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ReleaseLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                releaseStarted.TrySetResult();
                return Task.FromResult(new JobAttemptCompletionResult { Status = JobAttemptCompletionStatus.Applied });
            });
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            ConfigureDeadlineOptions,
            timeProvider: time);
        store.ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                time.Advance(TimeSpan.FromSeconds(34));
                return Task.FromResult<IReadOnlyList<JobExecutionLease>>(
                    [CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration)]);
            });

        await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
        await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        probe.Started.Task.IsCompleted.Should().BeFalse();
        await store.DidNotReceive().RenewLeaseAsync(
            Arg.Any<JobLeaseKey>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().CompleteAttemptAsync(
            Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLeaseAsync_WhenProcessPauseExhaustsLeaseSafety_ShouldRequestRestartWithoutAnotherGracePeriod()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CancellationProbe();
        using var host = CreateJobHost<CancellationIgnoringRecurringJob, CancellationProbe>(probe);
        var definition = CreateDefinition<CancellationIgnoringRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var applicationLifetime = CreateApplicationLifetime(out var restartRequested);
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            ConfigureDeadlineOptions,
            applicationLifetime,
            time);
        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration),
            TestContext.Current.CancellationToken);

        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(33), TestContext.Current.CancellationToken);
            time.AdvanceAfterPause(TimeSpan.FromSeconds(50));

            await restartRequested.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            attempt.IsCompleted.Should().BeFalse();
            await store.DidNotReceive().CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(
                Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            probe.Release.TrySetResult();
            await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteLeaseAsync_WhenUserCodeBlocksBeforeReturningATask_ShouldStartTheWatchdogBeforeDispatch()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CancellationProbe();
        using var host = CreateJobHost<SynchronouslyBlockingRecurringJob, CancellationProbe>(probe);
        var definition = CreateDefinition<SynchronouslyBlockingRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var applicationLifetime = CreateApplicationLifetime(out var restartRequested);
        var (worker, options) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            schedulerOptions =>
            {
                ConfigureDeadlineOptions(schedulerOptions);
                schedulerOptions.ExecutionLeaseRenewInterval = TimeSpan.FromSeconds(30);
            },
            applicationLifetime,
            time);
        var attempt = Task.Run(() => worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromSeconds(11), options.Value.ExecutionLeaseDuration),
            TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(11));
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(16), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(5));
            await restartRequested.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            applicationLifetime.Received(1).StopApplication();
            attempt.IsCompleted.Should().BeFalse();
            probe.Exited.Task.IsCompleted.Should().BeFalse();
            await store.DidNotReceive().CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(
                Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            probe.Release.TrySetResult();
            await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteLeaseAsync_WhenCancellationCallbackBlocks_ShouldRequestRestartAtTheGraceDeadline()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new BlockingCancellationProbe();
        using var host = CreateJobHost<BlockingCancellationRecurringJob, BlockingCancellationProbe>(probe);
        var definition = CreateDefinition<BlockingCancellationRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var applicationLifetime = CreateApplicationLifetime(out var restartRequested);
        var (worker, _) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            schedulerOptions =>
            {
                schedulerOptions.ExecutionLeaseRenewInterval = TimeSpan.FromSeconds(30);
                schedulerOptions.ExecutionCancellationGracePeriod = TimeSpan.FromSeconds(5);
            },
            applicationLifetime,
            time);
        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromSeconds(11)),
            TestContext.Current.CancellationToken);

        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(11));
            await probe.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(16), TestContext.Current.CancellationToken);

            time.Advance(TimeSpan.FromSeconds(5));
            await restartRequested.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            applicationLifetime.Received(1).StopApplication();
            attempt.IsCompleted.Should().BeFalse();
            probe.CallbackExited.Task.IsCompleted.Should().BeFalse();
            await store.DidNotReceive().CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(),
                Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(
                Arg.Any<JobLeaseKey>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            probe.ReleaseCallback.TrySetResult();
            await probe.CallbackExited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ClaimAvailableWorkAsync_WhenAnAttemptIgnoresCancellation_ShouldStopNewClaimsWithoutReleasingTheLease()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CancellationProbe();
        using var host = CreateJobHost<CancellationIgnoringRecurringJob, CancellationProbe>(probe);
        var definition = CreateDefinition<CancellationIgnoringRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        store.ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JobExecutionLease>>(
                [CreateLease(definition.Declaration.JobKey, TimeSpan.FromSeconds(11))]));
        var restartRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        applicationLifetime.When(lifetime => lifetime.StopApplication())
            .Do(_ => restartRequested.TrySetResult());
        var (worker, _) = CreateWorker(
            store,
            [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            schedulerOptions =>
            {
                schedulerOptions.MaxWorkerExecutionThreads = 2;
                schedulerOptions.ExecutionLeaseRenewInterval = TimeSpan.FromSeconds(30);
                schedulerOptions.ExecutionCancellationGracePeriod = TimeSpan.FromSeconds(5);
            },
            applicationLifetime,
            time);

        try
        {
            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(11));
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(16), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(5));
            await restartRequested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);

            await store.Received(1).ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>());
            probe.Exited.Task.IsCompleted.Should().BeFalse();
            await store.DidNotReceive().CompleteAttemptAsync(
                Arg.Any<JobAttemptCompletion>(),
                Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(
                Arg.Any<JobLeaseKey>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            probe.Release.TrySetResult();
            await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    private static IHostApplicationLifetime CreateApplicationLifetime(out Task restartRequested)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        applicationLifetime.When(lifetime => lifetime.StopApplication()).Do(_ => signal.TrySetResult());
        restartRequested = signal.Task;
        return applicationLifetime;
    }

    private static IHost CreateJobHost<TJob, TProbe>(TProbe probe)
        where TJob : class, IRecurringJob
        where TProbe : class
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddSingleton(probe);
        hostBuilder.Services.AddScoped<TJob>();
        hostBuilder.AddMonica(monica => monica.AddExecutionPipeline());
        return hostBuilder.Build();
    }

    private static LocalJobDefinition CreateDefinition<TJob>() where TJob : IRecurringJob => new()
    {
        JobClrType = typeof(TJob),
        Declaration = StoreFixture.RecurringDeclaration(typeof(TJob).FullName!)
    };

    private static (JobExecutionWorkerHostedService Worker, OptionsWrapper<ModuleJobSchedulerOption> Options)
        CreateWorker(
            IJobSchedulerStore store,
            IReadOnlyList<LocalJobDefinition> definitions,
            IServiceScopeFactory scopeFactory,
            Action<ModuleJobSchedulerOption>? configure,
            IHostApplicationLifetime? applicationLifetime = null,
            TimeProvider? timeProvider = null)
    {
        var options = new OptionsWrapper<ModuleJobSchedulerOption>(new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            ProjectName = OWNER,
            WorkerInstanceId = WORKER_INSTANCE,
            ExecutionLeaseDuration = TimeSpan.FromMinutes(1),
            ExecutionLeaseRenewInterval = TimeSpan.FromMilliseconds(5),
            ExecutionCancellationGracePeriod = TimeSpan.FromSeconds(5)
        });
        configure?.Invoke(options.Value);
        var schedulerOptions = Options.Create(options.Value);
        var orchestrator = new JobOrchestrator(
            scopeFactory,
            new JobExecutor(),
            new JobRegistry(definitions, NullLogger<JobRegistry>.Instance),
            store,
            schedulerOptions,
            NullLogger<JobOrchestrator>.Instance);
        var worker = new JobExecutionWorkerHostedService(
            store,
            definitions,
            orchestrator,
            new JobSchedulerRuntimeState(),
            timeProvider ?? TimeProvider.System,
            applicationLifetime ?? Substitute.For<IHostApplicationLifetime>(),
            schedulerOptions,
            new ObservableInstanceRegistry(Options.Create(new ModuleObservableInstanceOption())),
            Options.Create(new ModuleHostedServiceOption()),
            scopeFactory,
            NullLogger<JobExecutionWorkerHostedService>.Instance);
        return (worker, options);
    }

    private static JobLeaseRenewalResult CreateActiveRenewal(TimeSpan? leaseDuration = null) => new()
    {
        Status = JobLeaseRenewalStatus.Active,
        GrantedAtUtc = NOW,
        LeaseExpiresAtUtc = NOW.Add(leaseDuration ?? TimeSpan.FromMinutes(1))
    };

    private static void ConfigureDeadlineOptions(ModuleJobSchedulerOption schedulerOptions)
    {
        schedulerOptions.ExecutionLeaseDuration = TimeSpan.FromSeconds(40);
        schedulerOptions.ExecutionLeaseRenewInterval = TimeSpan.FromSeconds(7);
        schedulerOptions.ExecutionLeaseSafetyMargin = TimeSpan.FromSeconds(2);
        schedulerOptions.ExecutionCancellationGracePeriod = TimeSpan.FromSeconds(5);
        schedulerOptions.ExecutionLeaseRenewalTimeout = TimeSpan.FromSeconds(3);
        schedulerOptions.ExecutionFinalizationTimeout = TimeSpan.FromSeconds(4);
    }

    private static JobExecutionLease CreateLease(
        string jobKey,
        TimeSpan executionTimeout,
        TimeSpan? leaseDuration = null)
    {
        return new JobExecutionLease
        {
            Execution = new JobExecutionInstance
            {
                InstanceId = "execution-1",
                Template = new JobExecutionTemplate
                {
                    SchedulerScopeKey = StoreFixture.SCOPE,
                    OwnerKey = OWNER,
                    JobKey = jobKey,
                    JobName = jobKey,
                    JobType = JobType.Recurring,
                    MaxConcurrency = 1,
                    MaxExecutionTimeout = executionTimeout
                },
                AvailableAtUtc = NOW,
                State = JobExecutionState.Running,
                CreatedAtUtc = NOW,
                StartedAtUtc = NOW,
                LeaseExpiresAtUtc = NOW.Add(leaseDuration ?? TimeSpan.FromMinutes(1))
            },
            GrantedAtUtc = NOW,
            LeaseKey = new JobLeaseKey
            {
                SchedulerScopeKey = StoreFixture.SCOPE,
                InstanceId = "execution-1",
                WorkerInstanceId = WORKER_INSTANCE,
                LeaseToken = "lease-token"
            }
        };
    }

    private sealed class CancellationProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CompletionProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completion { get; } = new();
    }

    private sealed class BlockingCancellationProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CallbackExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingCancellationRecurringJob(BlockingCancellationProbe probe) : IRecurringJob
    {
        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                probe.CallbackEntered.TrySetResult();
                probe.ReleaseCallback.Task.GetAwaiter().GetResult();
                probe.CallbackExited.TrySetResult();
            });
            probe.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                probe.Exited.TrySetResult();
            }
        }
    }

    private sealed class CompletingRecurringJob(CompletionProbe probe) : IRecurringJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            probe.Started.TrySetResult();
            return probe.Completion.Task;
        }
    }

    private sealed class CancellationObservingRecurringJob(CancellationProbe probe) : IRecurringJob
    {
        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            probe.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                probe.Exited.TrySetResult();
            }
        }
    }

    private sealed class CancellationIgnoringRecurringJob(CancellationProbe probe) : IRecurringJob
    {
        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            probe.Started.TrySetResult();
            try
            {
                await probe.Release.Task;
            }
            finally
            {
                probe.Exited.TrySetResult();
            }
        }
    }

    private sealed class SynchronouslyBlockingRecurringJob(CancellationProbe probe) : IRecurringJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            probe.Started.TrySetResult();
            try
            {
                probe.Release.Task.GetAwaiter().GetResult();
                return Task.CompletedTask;
            }
            finally
            {
                probe.Exited.TrySetResult();
            }
        }
    }
}
