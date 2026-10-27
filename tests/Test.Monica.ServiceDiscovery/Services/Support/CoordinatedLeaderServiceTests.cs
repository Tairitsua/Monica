using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.ObservableInstance.Services;
using Monica.Locker.Leases;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Events;
using Monica.ServiceDiscovery.Services.Support;
using NSubstitute;
using Xunit;

namespace Test.Monica.ServiceDiscovery.Services.Support;

public sealed class CoordinatedLeaderServiceTests
{
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
}
