using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Services;
using Monica.Locker.Leases;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Events;
using Monica.ServiceDiscovery.Services.Support;
using Monica.Testing.Time;
using NSubstitute;
using Xunit;

namespace Test.Monica.ServiceDiscovery.Services.Support;

public sealed class CoordinatedLeaderServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Work_WhenInitializationOrExecutionFails_ShouldCleanupAndRetryWithinTheSameTerm(bool failExecution)
    {
        await using var fixture = new RetryFixture(failExecution);
        var failed = await fixture.Worker.ReadCleanupAsync();
        Assert.Equal(LeaderLostReason.WorkFailed, failed);
        var firstAttempt = Assert.Single(fixture.Worker.Attempts);
        Assert.True(firstAttempt.Token.IsCancellationRequested);
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromSeconds(5), Token);
        fixture.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.Single(fixture.Worker.Attempts);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));

        var recovered = await fixture.Worker.ReadSuccessfulWorkAsync();
        Assert.Equal(firstAttempt.Identity, recovered.Identity);
        Assert.NotEqual(firstAttempt.Token, recovered.Token);
        Assert.False(recovered.Token.IsCancellationRequested);
        Assert.Equal(2, fixture.Worker.Attempts.Count);
        fixture.Leadership.DidNotReceive().TriggerLeaderLost(Arg.Any<LeaderLostReason>());
    }

    [Fact]
    public async Task Retry_WhenTheTermIsLostDuringBackoff_ShouldStartTheSuccessorWithoutRetryingTheOldTerm()
    {
        await using var fixture = new RetryFixture();
        await fixture.Worker.ReadCleanupAsync();
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromSeconds(5), Token);
        await fixture.FirstLifetime.CancelAsync();
        fixture.PublishSecondTerm();

        var successor = await fixture.Worker.ReadSuccessfulWorkAsync();
        Assert.Equal(fixture.Second.Identity, successor.Identity);
        fixture.Time.Advance(TimeSpan.FromSeconds(20));
        await fixture.StopAsync();
        Assert.Equal([fixture.First.Identity, fixture.Second.Identity], fixture.Worker.Attempts.Select(attempt => attempt.Identity));
    }

    [Fact]
    public async Task Retry_WhenTheHostStopsDuringBackoff_ShouldStopWithoutAnotherAttempt()
    {
        await using var fixture = new RetryFixture();
        await fixture.Worker.ReadCleanupAsync();
        await fixture.Time.WaitForTimerAsync(TimeSpan.FromSeconds(5), Token);
        await fixture.StopAsync();

        fixture.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.Single(fixture.Worker.Attempts);
        Assert.False(fixture.First.StoppingToken.IsCancellationRequested);
        fixture.Leadership.DidNotReceive().TriggerLeaderLost(Arg.Any<LeaderLostReason>());
    }

    [Fact]
    public async Task Retry_WhenFailureCleanupIsBlocked_ShouldDrainBeforeStartingASuccessorTerm()
    {
        await using var fixture = new RetryFixture(blockCleanup: true);
        await fixture.Worker.ReadCleanupAsync();
        await fixture.FirstLifetime.CancelAsync();
        fixture.PublishSecondTerm();
        Assert.Single(fixture.Worker.Attempts);
        fixture.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.Single(fixture.Worker.Attempts);

        fixture.Worker.AllowCleanup.TrySetResult();
        var successor = await fixture.Worker.ReadSuccessfulWorkAsync();
        Assert.Equal(fixture.Second.Identity, successor.Identity);
        Assert.Equal(2, fixture.Worker.Attempts.Count);
    }

    [Fact]
    public async Task Cleanup_WhenItFails_ShouldRequireRestartEvenAfterANewTermArrives()
    {
        await using var fixture = new RetryFixture(blockCleanup: true, failCleanup: true);
        await fixture.Worker.ReadCleanupAsync();
        await fixture.FirstLifetime.CancelAsync();
        fixture.PublishSecondTerm();
        fixture.Worker.AllowCleanup.TrySetResult();

        // Completion proves the queued successor was processed after the failed cleanup, without starting it.
        await fixture.WaitForCompletionAsync();
        Assert.Single(fixture.Worker.Attempts);
        Assert.Equal(HostedServiceState.Faulted, fixture.Worker.RuntimeInfo.CurrentState);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        fixture.PublishSecondTerm();
        await fixture.StopAsync();
        Assert.Single(fixture.Worker.Attempts);
    }

    [Fact]
    public async Task LossDuringInitialization_ShouldCancelImmediatelyAndDrainBeforeTheNextTermStarts()
    {
        var token = TestContext.Current.CancellationToken;
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var firstLifetime = new CancellationTokenSource();
        using var secondLifetime = new CancellationTokenSource();
        var leadership = Substitute.For<ILeaderElectionService>();
        var resource = new LeaseResource("test", "leader");
        var first = new LeaseContext(new(resource, "holder", "first"), firstLifetime.Token);
        var second = new LeaseContext(new(resource, "holder", "second"), secondLifetime.Token);
        leadership.CurrentLease.Returns(first);
        using var worker = new Worker(leadership, provider.GetRequiredService<IServiceScopeFactory>());
        var running = worker.RunAsync(stop.Token);
        try
        {
            await worker.FirstInitializing.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await firstLifetime.CancelAsync();
            await worker.Draining.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.True(worker.FirstToken.IsCancellationRequested);
            leadership.OnLeaderGained += Raise.Event<EventHandler<LeaderGainedEvent>>(leadership, new LeaderGainedEvent
            {
                AppId = "app", InstanceId = "holder", BecomeLeaderTime = DateTime.UtcNow, Lease = second
            });
            Assert.False(worker.SecondInitializing.Task.IsCompleted);
            worker.AllowDrain.TrySetResult();
            await worker.SecondInitializing.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(worker.SecondToken.IsCancellationRequested);
            Assert.True(worker.FirstToken.IsCancellationRequested);
        }
        finally
        {
            worker.AllowDrain.TrySetResult();
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
    }

    private sealed class Worker(ILeaderElectionService leadership, IServiceScopeFactory scopes)
        : CoordinatedLeaderService(leadership, Microsoft.Extensions.Options.Options.Create(new ModuleServiceDiscoveryOption { SkipRegistrationWait = true }),
            Substitute.For<IServiceRegistrationCoordinator>(), new ObservableInstanceRegistry(Microsoft.Extensions.Options.Options.Create(new ModuleObservableInstanceOption())),
            Microsoft.Extensions.Options.Options.Create(new ModuleHostedServiceOption()), scopes, NullLogger.Instance)
    {
        private int _initializations;
        public TaskCompletionSource FirstInitializing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondInitializing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Draining { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDrain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken FirstToken { get; private set; }
        public CancellationToken SecondToken { get; private set; }
        public Task RunAsync(CancellationToken token) => ExecuteBackgroundAsync(token);

        protected override async Task OnBecameLeaderAsync(CancellationToken leaderToken)
        {
            if (Interlocked.Increment(ref _initializations) == 1)
            {
                FirstToken = leaderToken;
                FirstInitializing.TrySetResult();
                await Task.Delay(Timeout.Infinite, leaderToken);
            }
            else
            {
                SecondToken = leaderToken;
                SecondInitializing.TrySetResult();
            }
        }

        protected override async Task OnLeaderLostAsync(LeaderLostReason reason)
        {
            Draining.TrySetResult();
            await AllowDrain.Task;
        }
    }

    private sealed class RetryFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider = new ServiceCollection().BuildServiceProvider();
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        public CancellationTokenSource FirstLifetime { get; } = new();
        private readonly CancellationTokenSource _secondLifetime = new();
        public ILeaderElectionService Leadership { get; } = Substitute.For<ILeaderElectionService>();
        public ControllableTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero));
        public LeaseContext First { get; }
        public LeaseContext Second { get; }
        public RetryWorker Worker { get; }
        private readonly Task _running;

        public RetryFixture(bool failExecution = false, bool blockCleanup = false, bool failCleanup = false)
        {
            var resource = new LeaseResource("test", "leader");
            First = new LeaseContext(new(resource, "holder", "first"), FirstLifetime.Token);
            Second = new LeaseContext(new(resource, "holder", "second"), _secondLifetime.Token);
            Leadership.CurrentLease.Returns(First);
            Worker = new RetryWorker(Leadership, _provider.GetRequiredService<IServiceScopeFactory>(), Time, failExecution, failCleanup);
            if (!blockCleanup) Worker.AllowCleanup.TrySetResult();
            _running = Worker.RunAsync(_stop.Token);
        }

        public void PublishSecondTerm()
        {
            Leadership.CurrentLease.Returns(Second);
            Leadership.OnLeaderGained += Raise.Event<EventHandler<LeaderGainedEvent>>(Leadership, new LeaderGainedEvent
            {
                AppId = "app", InstanceId = "holder", BecomeLeaderTime = Time.GetUtcNow().UtcDateTime, Lease = Second
            });
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            await WaitForCompletionAsync();
        }

        public Task WaitForCompletionAsync() => _running.WaitAsync(TimeSpan.FromSeconds(5), Token);

        public async ValueTask DisposeAsync()
        {
            Worker.AllowCleanup.TrySetResult();
            await StopAsync();
            Worker.Dispose();
            FirstLifetime.Dispose();
            _secondLifetime.Dispose();
            _stop.Dispose();
            await _provider.DisposeAsync();
        }
    }

    private sealed record Attempt(LeaseIdentity Identity, CancellationToken Token);

    private sealed class RetryWorker(ILeaderElectionService leadership, IServiceScopeFactory scopes,
        TimeProvider time, bool failExecution, bool failCleanup)
        : CoordinatedLeaderService(leadership, Microsoft.Extensions.Options.Options.Create(new ModuleServiceDiscoveryOption { SkipRegistrationWait = true }),
            Substitute.For<IServiceRegistrationCoordinator>(), new ObservableInstanceRegistry(Microsoft.Extensions.Options.Options.Create(new ModuleObservableInstanceOption())),
            Microsoft.Extensions.Options.Options.Create(new ModuleHostedServiceOption()), scopes, NullLogger.Instance, time)
    {
        public ConcurrentQueue<Attempt> Attempts { get; } = new();
        public TaskCompletionSource AllowCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<LeaderLostReason> _cleanups = Channel.CreateUnbounded<LeaderLostReason>();
        private readonly Channel<Attempt> _successfulWork = Channel.CreateUnbounded<Attempt>();

        public Task RunAsync(CancellationToken token) => ExecuteBackgroundAsync(token);
        public Task<LeaderLostReason> ReadCleanupAsync() => _cleanups.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        public Task<Attempt> ReadSuccessfulWorkAsync() => _successfulWork.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);

        protected override Task OnBecameLeaderAsync(CancellationToken leaderToken)
        {
            Attempts.Enqueue(new Attempt(LeaderService.CurrentLease!.Identity, leaderToken));
            if (!failExecution && Attempts.Count == 1) throw new IOException("Transient initialization failure");
            return Task.CompletedTask;
        }

        protected override Task LeaderExecuteBackgroundAsync(CancellationToken leaderToken)
        {
            if (failExecution && Attempts.Count == 1) throw new IOException("Transient background failure");
            _successfulWork.Writer.TryWrite(Attempts.Last());
            return Task.CompletedTask;
        }

        protected override async Task OnLeaderLostAsync(LeaderLostReason reason)
        {
            _cleanups.Writer.TryWrite(reason);
            await AllowCleanup.Task;
            if (failCleanup) throw new IOException("Cleanup did not release its resources");
        }
    }
}
