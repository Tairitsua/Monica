using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Locker.Leases;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Events;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Services.Support;
using Monica.Testing.Time;
using NSubstitute;
using Xunit;

namespace Test.Monica.ServiceDiscovery.Services.Support;

public sealed class LeaderLeaseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Leadership_ShouldRenewWithoutAnotherHeartbeatAndKeepItsAcquisitionTime()
    {
        await using var fixture = new Fixture();
        var first = await fixture.GainedAsync();
        var becameLeader = fixture.Service.LeaderBecomeTime;
        await fixture.TimerAsync(10);
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        await fixture.TimerAsync(20);
        Assert.Same(first.Lease, fixture.Service.CurrentLease);
        Assert.Equal(becameLeader, fixture.Service.LeaderBecomeTime);
        var snapshot = await fixture.Store.GetSnapshotAsync("service-discovery", "", Token);
        Assert.Equal(fixture.Start.AddSeconds(40), Assert.Single(snapshot.Leases).ExpiresAtUtc);
    }

    [Fact]
    public async Task ExpiredTerm_ShouldCancelPermanentlyAndEmitLostBeforeAFreshGainedEvent()
    {
        await using var fixture = new Fixture();
        var first = await fixture.GainedAsync();
        await fixture.TimerAsync(10);
        fixture.Time.AdvanceAfterPause(TimeSpan.FromSeconds(31));
        var lost = await fixture.LostAsync();
        Assert.Equal(first.Lease.Identity, lost.Identity);
        Assert.True(first.Lease.StoppingToken.IsCancellationRequested);
        fixture.Service.SetRegistrationAvailable(true);
        await fixture.TimerAsync(36);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        var second = await fixture.GainedAsync();
        Assert.NotEqual(first.Lease.Identity.TermId, second.Lease.Identity.TermId);
        Assert.False(second.Lease.StoppingToken.IsCancellationRequested);
        Assert.True(first.Lease.StoppingToken.IsCancellationRequested);
        Assert.False(await fixture.Store.TryReleaseAsync(first.Lease.Identity, Token));
    }

    [Fact]
    public async Task RenewalFailure_ShouldEndTheTermImmediatelyWithoutReadingOrAdoptingAnotherToken()
    {
        await using var fixture = new Fixture(failRenewal: true);
        var first = await fixture.GainedAsync();
        await fixture.TimerAsync(10);
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        var lost = await fixture.LostAsync();
        Assert.Equal(first.Lease.Identity, lost.Identity);
        Assert.True(first.Lease.StoppingToken.IsCancellationRequested);
        Assert.False(fixture.Service.IsLeader);
        // The original authority still holds the grant: ambiguous failure does not imply safe early release.
        Assert.Null(await fixture.Store.TryAcquireAsync(first.Lease.Identity.Resource, "other", TimeSpan.FromSeconds(30), Token));
    }

    [Fact]
    public async Task LocalResignationAndOldHostShutdown_ShouldNeverDeleteTheReplacementLeadersGrant()
    {
        await using var fixture = new Fixture();
        var first = await fixture.GainedAsync();
        fixture.Service.SetRegistrationAvailable(false);
        await fixture.LostAsync();
        Assert.True(first.Lease.StoppingToken.IsCancellationRequested);
        Assert.Null(await fixture.Store.TryAcquireAsync(first.Lease.Identity.Resource, "other", TimeSpan.FromSeconds(30), Token));
        fixture.Time.AdvanceAfterPause(TimeSpan.FromSeconds(31));
        var replacement = Assert.IsType<LeaseGrant>(await fixture.Store.TryAcquireAsync(
            first.Lease.Identity.Resource, "other", TimeSpan.FromSeconds(30), Token));
        fixture.Service.Dispose();
        var observation = Assert.Single((await fixture.Store.GetSnapshotAsync("service-discovery", "", Token)).Leases);
        Assert.Equal(replacement.Identity.HolderId, observation.HolderId);
    }

    [Fact]
    public async Task WorkerRole_ShouldPublishMembershipWithoutAcquiringLeadership()
    {
        await using var fixture = new Fixture(role: ServiceDiscoveryRole.Worker);
        await fixture.Running;
        Assert.False(fixture.Service.IsLeader);
        Assert.Empty((await fixture.Store.GetSnapshotAsync("service-discovery", "", Token)).Leases);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public DateTimeOffset Start { get; } = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
        public ControllableTimeProvider Time { get; }
        public InMemoryLeaseStore Store { get; }
        public LeaderElectionService Service { get; }
        public Task Running { get; }
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        private readonly Channel<LeaderGainedEvent> _gained = Channel.CreateUnbounded<LeaderGainedEvent>();
        private readonly Channel<LeaderLostEvent> _lost = Channel.CreateUnbounded<LeaderLostEvent>();

        public Fixture(bool failRenewal = false, ServiceDiscoveryRole role = ServiceDiscoveryRole.Registry)
        {
            Time = new ControllableTimeProvider(Start);
            Store = new InMemoryLeaseStore(Time);
            var info = Substitute.For<IServiceDiscoveryClientInfo>();
            info.GetServiceStatus(Arg.Any<bool>()).Returns(new InstanceState
                { AppId = "app", InstanceId = "instance", AppName = "Application", ProjectName = "Project" });
            var options = Options.Create(new ModuleServiceDiscoveryOption
            {
                Role = role,
                Election = new ElectionConfig
                {
                    HeartbeatPeriodSeconds = 10, LeaderOfflineTTLMultiplier = 3, LeaderOfflineTTLAdditionalSeconds = 0
                }
            });
            IExclusiveLeaseStore backend = Store;
            if (failRenewal)
            {
                backend = Substitute.For<IExclusiveLeaseStore>();
                backend.TryAcquireAsync(Arg.Any<LeaseResource>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                    .Returns(call => Store.TryAcquireAsync(call.Arg<LeaseResource>(), call.Arg<string>(), call.Arg<TimeSpan>(), call.Arg<CancellationToken>()));
                backend.TryRenewAsync(Arg.Any<LeaseIdentity>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromException<LeaseGrant?>(new IOException("Ambiguous renewal failure")));
            }
            Service = new LeaderElectionService(info, options, NullLogger<LeaderElectionService>.Instance, backend, Time);
            Service.OnLeaderGained += (_, notification) => _gained.Writer.TryWrite(notification);
            Service.OnLeaderLost += (_, notification) => _lost.Writer.TryWrite(notification);
            Service.SetRegistrationAvailable(true);
            Running = Service.RunAsync(_stop.Token);
        }

        public Task<LeaderGainedEvent> GainedAsync() => _gained.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        public Task<LeaderLostEvent> LostAsync() => _lost.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        public Task TimerAsync(int second) => Time.WaitForTimerAsync(TimeSpan.FromSeconds(second), Token).WaitAsync(TimeSpan.FromSeconds(5), Token);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Running.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Service.Dispose();
            _stop.Dispose();
        }
    }
}
