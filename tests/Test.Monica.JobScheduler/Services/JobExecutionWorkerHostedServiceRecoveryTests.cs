using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models.Execution;
using Monica.Testing.Time;
using NSubstitute;
using Xunit;

namespace Test.Monica.JobScheduler.Services;

public sealed partial class JobExecutionWorkerHostedServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteLeaseAsync_WhenConcurrentDrainsIncludeBlockedCallbacks_ShouldRetainAdmissionUntilEveryCallExits(bool exhaustDrain)
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CompletionProbe();
        using var host = CreateJobHost<CompletingRecurringJob, CompletionProbe>(probe);
        var definition = CreateDefinition<CompletingRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackIoExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 2).Select(_ => new TaskCompletionSource<JobAttemptCompletionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var count = 0;
        var registration = default(CancellationTokenRegistration);
        store.CompleteAttemptAsync(Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var index = Interlocked.Increment(ref count) - 1;
                if (index == 1)
                {
                    registration = call.Arg<CancellationToken>().Register(() =>
                    {
                        callbackEntered.TrySetResult();
                        releaseCallback.Task.GetAwaiter().GetResult();
                    });
                    bothStarted.TrySetResult();
                }
                var result = await calls[index].Task;
                if (index == 1) callbackIoExited.TrySetResult();
                return result;
            });
        var lifetime = CreateApplicationLifetime(out var restartRequested);
        var (worker, options) = CreateWorker(store, [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(), ConfigureDeadlineOptions, lifetime, time);
        var attempts = Enumerable.Range(0, 2).Select(_ => worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration),
            TestContext.Current.CancellationToken)).ToArray();
        try
        {
            probe.Completion.TrySetResult();
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(4));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(34), TestContext.Current.CancellationToken);
            foreach (var call in calls)
                call.TrySetResult(new JobAttemptCompletionResult { Status = JobAttemptCompletionStatus.Applied });
            await callbackIoExited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await (await Task.WhenAny(attempts)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task.WhenAll(attempts).IsCompleted.Should().BeFalse("the second I/O returned but its callback still owns resources");
            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
            await store.DidNotReceive().ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>());
            lifetime.DidNotReceive().StopApplication();

            if (exhaustDrain)
            {
                time.Advance(options.Value.ExecutionStoreDrainTimeout);
                await restartRequested.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Task.WhenAll(attempts).IsCompleted.Should().BeFalse();
            }
            releaseCallback.TrySetResult();
            await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            time.Advance(options.Value.ExecutionStoreRecoveryDelay);
            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
            if (exhaustDrain)
                await store.DidNotReceive().ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>());
            else
            {
                await store.Received(1).ClaimAsync(Arg.Is<JobClaimRequest>(request => request.MaxCount == 1), Arg.Any<CancellationToken>());
                lifetime.DidNotReceive().StopApplication();
            }
        }
        finally
        {
            releaseCallback.TrySetResult();
            foreach (var call in calls)
                call.TrySetResult(new JobAttemptCompletionResult { Status = JobAttemptCompletionStatus.Applied });
            await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            registration.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteLeaseAsync_WhenCancelledRenewalDrainsAfterJobExit_ShouldBackOffOnlyAfterTimeout(bool timedOut)
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CompletionProbe();
        using var host = CreateJobHost<CompletingRecurringJob, CompletionProbe>(probe);
        var definition = CreateDefinition<CompletingRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = new TaskCompletionSource<JobLeaseRenewalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalToken = CancellationToken.None;
        store.RenewLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                renewalToken = call.Arg<CancellationToken>();
                started.TrySetResult();
                return renewal.Task;
            });
        store.CompleteAttemptAsync(Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>())
            .Returns(new JobAttemptCompletionResult { Status = JobAttemptCompletionStatus.Applied });
        var lifetime = CreateApplicationLifetime(out _);
        var (worker, options) = CreateWorker(store, [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(), ConfigureDeadlineOptions, lifetime, time);
        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration),
            TestContext.Current.CancellationToken);

        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(7));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (timedOut) time.Advance(TimeSpan.FromSeconds(3));
            probe.Completion.TrySetResult();
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(timedOut ? 40 : 37), TestContext.Current.CancellationToken);
            renewalToken.IsCancellationRequested.Should().BeTrue();
            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
            await store.DidNotReceive().ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>());
            attempt.IsCompleted.Should().BeFalse();
            lifetime.DidNotReceive().StopApplication();

            time.Advance(TimeSpan.FromMilliseconds(230));
            renewal.TrySetException(new OperationCanceledException(renewalToken));
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await store.Received(1).CompleteAttemptAsync(Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>());
            if (timedOut)
            {
                await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
                await store.DidNotReceive().ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>());
                time.Advance(options.Value.ExecutionStoreRecoveryDelay);
            }
            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
            await store.Received(1).ClaimAsync(Arg.Is<JobClaimRequest>(request => request.MaxCount == 1),
                Arg.Any<CancellationToken>());
            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
            await store.Received(1).ClaimAsync(Arg.Is<JobClaimRequest>(request => request.MaxCount > 1),
                Arg.Any<CancellationToken>());
            lifetime.DidNotReceive().StopApplication();
        }
        finally
        {
            probe.Completion.TrySetResult();
            renewal.TrySetResult(CreateActiveRenewal());
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteLeaseAsync_WhenRenewalDrainsAfterLeaseRetirement_ShouldNotReviveOrFinalizeTheAttempt()
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CancellationProbe();
        using var host = CreateJobHost<CancellationObservingRecurringJob, CancellationProbe>(probe);
        var definition = CreateDefinition<CancellationObservingRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = new TaskCompletionSource<JobLeaseRenewalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.RenewLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => { started.TrySetResult(); return renewal.Task; });
        var lifetime = CreateApplicationLifetime(out _);
        var (worker, options) = CreateWorker(store, [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(), ConfigureDeadlineOptions, lifetime, time);
        using var stopping = new CancellationTokenSource();
        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration), stopping.Token);
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(7));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(3));
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(40), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(23));
            await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            attempt.IsCompleted.Should().BeFalse();
            renewal.TrySetResult(CreateActiveRenewal(TimeSpan.FromHours(1)));
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            await store.DidNotReceive().CompleteAttemptAsync(Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>());
            _ = store.Received(1).RenewLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            lifetime.DidNotReceive().StopApplication();
        }
        finally
        {
            renewal.TrySetResult(CreateActiveRenewal());
            await stopping.CancelAsync();
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteLeaseAsync_WhenFinalizationTimesOutButDrains_ShouldNotReplayAnUnknownCommit(bool commitSucceeded)
    {
        var time = new ControllableTimeProvider(NOW);
        var probe = new CompletionProbe();
        using var host = CreateJobHost<CompletingRecurringJob, CompletionProbe>(probe);
        var definition = CreateDefinition<CompletingRecurringJob>();
        var store = Substitute.For<IJobSchedulerStore>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<JobAttemptCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.CompleteAttemptAsync(Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>())
            .Returns(_ => { started.TrySetResult(); return completion.Task; });
        var lifetime = CreateApplicationLifetime(out _);
        var (worker, options) = CreateWorker(store, [definition],
            host.Services.GetRequiredService<IServiceScopeFactory>(), ConfigureDeadlineOptions, lifetime, time);
        var attempt = worker.ExecuteLeaseAsync(
            CreateLease(definition.Declaration.JobKey, TimeSpan.FromHours(1), options.Value.ExecutionLeaseDuration),
            TestContext.Current.CancellationToken);
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            probe.Completion.TrySetResult();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(4));
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(34), TestContext.Current.CancellationToken);
            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
            await store.DidNotReceive().ClaimAsync(Arg.Any<JobClaimRequest>(), Arg.Any<CancellationToken>());
            lifetime.DidNotReceive().StopApplication();
            if (commitSucceeded)
            {
                completion.TrySetResult(new JobAttemptCompletionResult { Status = JobAttemptCompletionStatus.Applied });
            }
            else
            {
                completion.TrySetException(new IOException("Commit acknowledgement was lost."));
            }
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            time.Advance(options.Value.ExecutionStoreRecoveryDelay);
            await worker.ClaimAvailableWorkAsync(TestContext.Current.CancellationToken);
            await store.Received(1).ClaimAsync(Arg.Is<JobClaimRequest>(request => request.MaxCount == 1), Arg.Any<CancellationToken>());
            _ = store.Received(1).CompleteAttemptAsync(Arg.Any<JobAttemptCompletion>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().ReleaseLeaseAsync(Arg.Any<JobLeaseKey>(), Arg.Any<CancellationToken>());
            lifetime.DidNotReceive().StopApplication();
        }
        finally
        {
            probe.Completion.TrySetResult();
            completion.TrySetResult(new JobAttemptCompletionResult { Status = JobAttemptCompletionStatus.Applied });
            await attempt.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }
}
