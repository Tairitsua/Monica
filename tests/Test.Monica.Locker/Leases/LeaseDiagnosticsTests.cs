using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Results;
using Monica.Locker.Facades;
using Monica.Locker.Leases;
using Monica.Locker.Leases.Diagnostics;
using Monica.Modules;
using Monica.Testing.Time;
using NSubstitute;
using Test.Monica.Locker.Support;
using Xunit;

namespace Test.Monica.Locker.Leases;

public sealed class LeaseDiagnosticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);
    private static readonly LeaseResource Resource = new("orders", "maintenance:outbox");
    private static LeaseSnapshot Snapshot => new(Now,
        [new(Resource, "worker-a", Now.AddSeconds(-10), Now, Now.AddSeconds(30))]);

    [Fact]
    public async Task ConcurrentViewers_ShouldShareOneInventoryRead_WhileExactReadRemainsIndependent()
    {
        var store = Substitute.For<IExclusiveLeaseStore>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<LeaseSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return completion.Task;
        });
        store.GetSnapshotAsync(Resource, Arg.Any<CancellationToken>()).Returns(Snapshot);
        await using var app = await Factory(store).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var scopes = Enumerable.Range(0, 24).Select(_ => app.Services.CreateAsyncScope()).ToArray();
        try
        {
            var readers = scopes.Select(scope => scope.ServiceProvider.GetRequiredService<LeaseDiagnosticsFacade>()).ToArray();
            var reads = readers.Select(reader => reader.GetInventoryAsync(TestContext.Current.CancellationToken)).ToArray();
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var exact = await readers[0].GetResourceAsync("primary", Resource, TestContext.Current.CancellationToken);
            Assert.True(exact.IsOk());
            Assert.False(reads[0].IsCompleted);
            completion.SetResult(Snapshot);
            var inventories = await Task.WhenAll(reads);
            Assert.All(inventories, result => Assert.Equal(Now, Assert.Single(result.Data!).Snapshot!.ObservedAtUtc));
            await store.Received(1).GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>());
            await store.Received(1).GetSnapshotAsync(Resource, Arg.Any<CancellationToken>());
        }
        finally
        {
            completion.TrySetResult(Snapshot);
            foreach (var scope in scopes) await scope.DisposeAsync();
        }
    }

    [Fact]
    public async Task FailedRefresh_ShouldRetainSourceTime_AndThrottleFailuresUsingMonotonicTime()
    {
        var clock = new ControllableTimeProvider(Now);
        var store = Substitute.For<IExclusiveLeaseStore>();
        store.GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>()).Returns(Snapshot);
        await using var app = await Factory(store, clock).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.Services.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<LeaseDiagnosticsFacade>();
        await facade.GetInventoryAsync(TestContext.Current.CancellationToken);
        clock.AdjustWallClock(TimeSpan.FromHours(4));
        await facade.GetInventoryAsync(TestContext.Current.CancellationToken);
        await store.Received(1).GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>());
        clock.Advance(TimeSpan.FromSeconds(15));
        store.GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<LeaseSnapshot>(new InvalidOperationException("Provider is unavailable.")));
        var failure = await facade.GetInventoryAsync(TestContext.Current.CancellationToken);
        var retained = Assert.Single(failure.Data!);
        Assert.True(retained.RefreshFailed);
        Assert.Equal(Now, retained.Snapshot!.ObservedAtUtc);
        Assert.Single(retained.Snapshot.Leases);
        await facade.GetInventoryAsync(TestContext.Current.CancellationToken);
        await store.Received(2).GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>());
        clock.Advance(TimeSpan.FromSeconds(15));
        store.GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>()).Returns(new LeaseSnapshot(Now.AddSeconds(30), []));
        var recovered = Assert.Single((await facade.GetInventoryAsync(TestContext.Current.CancellationToken)).Data!);
        Assert.False(recovered.RefreshFailed);
        Assert.Empty(recovered.Snapshot!.Leases);
    }

    [Fact]
    public async Task EveryFacadeOperation_ShouldRecheckAccess_EvenWithCachedEvidence()
    {
        var access = new Access { Allowed = true };
        var store = Substitute.For<IExclusiveLeaseStore, ILeaseAdministration>();
        store.GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>()).Returns(Snapshot);
        await using var app = await Factory(store, access: access).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.Services.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<LeaseDiagnosticsFacade>();
        Assert.True((await facade.GetInventoryAsync(TestContext.Current.CancellationToken)).IsOk());
        store.ClearReceivedCalls();
        access.Allowed = false;
        Assert.Equal(ResStatus.Forbidden, (await facade.GetInventoryAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(ResStatus.Forbidden, (await facade.GetResourceAsync("primary", Resource, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(ResStatus.Forbidden, (await facade.RevokeAsync("primary", Resource, TestContext.Current.CancellationToken)).Status);
        Assert.Empty(store.ReceivedCalls());
    }

    [Fact]
    public async Task DefaultAccess_ShouldDenyWithoutTouchingProvider()
    {
        var store = Substitute.For<IExclusiveLeaseStore>();
        var factory = new LockerTestApplicationFactory(builder => builder.AddLeaseDiagnostics(options => options.Authorities.Add(Authority)),
            services => services.AddKeyedSingleton("diagnostic-store", store));
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<LeaseDiagnosticsFacade>().GetInventoryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ResStatus.Forbidden, result.Status);
        Assert.Empty(store.ReceivedCalls());
    }

    [Fact]
    public async Task Binding_ShouldContainReadsAndMutations_AndNotInferRevocationFromProviderKind()
    {
        var store = Substitute.For<IExclusiveLeaseStore>();
        store.GetSnapshotAsync(Resource, Arg.Any<CancellationToken>()).Returns(Snapshot);
        await using var app = await Factory(store).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.Services.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<LeaseDiagnosticsFacade>();
        Assert.False((await facade.GetResourceAsync("primary", new("other", Resource.Name), TestContext.Current.CancellationToken)).IsOk());
        Assert.False((await facade.RevokeAsync("primary", new("orders", "unrelated:role"), TestContext.Current.CancellationToken)).IsOk());
        Assert.Empty(store.ReceivedCalls());
        var exact = await facade.GetResourceAsync("primary", Resource, TestContext.Current.CancellationToken);
        Assert.False(exact.Data!.CanRevoke);
        Assert.Equal(ResStatus.Forbidden, (await facade.RevokeAsync("primary", Resource, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task CancelledProviderThatIgnoresToken_ShouldKeepAdmissionUntilItsRealTaskCompletes()
    {
        var store = Substitute.For<IExclusiveLeaseStore>();
        var completion = new TaskCompletionSource<LeaseSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>()).Returns(completion.Task);
        await using var app = await Factory(store).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.Services.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<LeaseDiagnosticsFacade>();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var abandoned = facade.GetInventoryAsync(caller.Token);
        await caller.CancelAsync();
        var next = facade.GetInventoryAsync(TestContext.Current.CancellationToken);
        Assert.False(abandoned.IsCompleted);
        Assert.False(next.IsCompleted);
        await store.Received(1).GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>());
        completion.SetResult(Snapshot);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.True((await next).IsOk());
        await store.Received(2).GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revocation_ShouldInvalidateAnyEarlierInventory_AndReadBackTheRemainingResources()
    {
        var store = Substitute.For<IExclusiveLeaseStore, ILeaseAdministration>();
        var inventory = new TaskCompletionSource<LeaseSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>()).Returns(inventory.Task);
        ((ILeaseAdministration)store).RevokeAsync(Resource, Arg.Any<CancellationToken>()).Returns(true);
        await using var app = await Factory(store).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.Services.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<LeaseDiagnosticsFacade>();
        var scan = facade.GetInventoryAsync(TestContext.Current.CancellationToken);
        var revoke = facade.RevokeAsync("primary", Resource, TestContext.Current.CancellationToken);
        Assert.False(revoke.IsCompleted);
        inventory.SetResult(Snapshot);
        await scan;
        Assert.True((await revoke).Data);
        store.GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>()).Returns(new LeaseSnapshot(Now.AddSeconds(1), []));
        var after = await facade.GetInventoryAsync(TestContext.Current.CancellationToken);
        Assert.Empty(Assert.Single(after.Data!).Snapshot!.Leases);
        await store.Received(2).GetSnapshotAsync("orders", "maintenance:", Arg.Any<CancellationToken>());
    }

    private static LeaseAuthority Authority => new("primary", "Orders SQL", "diagnostic-store", "orders", "maintenance:", LeaseAuthorityKind.Sql, allowRevocation: true);

    private static LockerTestApplicationFactory Factory(IExclusiveLeaseStore store, TimeProvider? time = null, Access? access = null) =>
        new(builder => builder.AddLeaseDiagnostics(options => options.Authorities.Add(Authority)), services =>
        {
            services.AddKeyedSingleton("diagnostic-store", store);
            services.AddSingleton(time ?? new ControllableTimeProvider(Now));
            services.AddScoped<ILeaseDiagnosticsAccess>(_ => access ?? new Access { Allowed = true });
        });

    private sealed class Access : ILeaseDiagnosticsAccess
    {
        public bool Allowed { get; set; }
        public Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Allowed);
    }
}
