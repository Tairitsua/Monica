using Microsoft.Extensions.DependencyInjection;
using Monica.Locker.Leases;
using Monica.Locker.Redis;
using StackExchange.Redis;
using Xunit;

namespace Test.Monica.Locker.Redis;

[Trait("Category", "ExternalRedis")]
public sealed class RedisLeaseStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan DURATION = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task ConcurrentAcquisition_ShouldHaveOneWinnerAndRejectStaleTermsEvenForTheSameHolder()
    {
        using var connection = await ConnectAsync();
        var options = Options();
        var stores = Enumerable.Range(0, 16).Select(_ => new RedisLeaseStore(connection, options)).ToArray();
        var resource = new LeaseResource("deployment", "leader");
        var results = await Task.WhenAll(stores.Select(store => store.TryAcquireAsync(resource, "same-holder", DURATION, Token)));
        var first = Assert.Single(results.OfType<LeaseGrant>());
        var store = stores[0];
        Assert.Null(await store.TryAcquireAsync(resource, "same-holder", DURATION, Token));
        Assert.True(await store.RevokeAsync(resource, Token));
        var next = Assert.IsType<LeaseGrant>(await store.TryAcquireAsync(resource, "same-holder", DURATION, Token));
        Assert.NotEqual(first.Identity, next.Identity);
        Assert.Null(await store.TryRenewAsync(first.Identity, DURATION, Token));
        Assert.False(await store.TryReleaseAsync(first.Identity, Token));
        Assert.Equal(next.Identity, (await store.TryRenewAsync(next.Identity, DURATION, Token))!.Identity);
        Assert.True(await store.TryReleaseAsync(next.Identity, Token));
        Assert.Empty((await store.GetSnapshotAsync(resource.Scope, "", Token)).Leases);
    }

    [Fact]
    public async Task ExpiredGrant_ShouldNotBeRenewedOrReusedAndShouldDisappearFromObservation()
    {
        using var connection = await ConnectAsync();
        var store = new RedisLeaseStore(connection, Options());
        var resource = new LeaseResource("expiry", "role");
        var first = Assert.IsType<LeaseGrant>(await store.TryAcquireAsync(resource, "holder", TimeSpan.FromMilliseconds(100), Token));
        // Real server expiry is the behavior under test; process-clock advancement cannot prove it.
        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        Assert.Null(await store.TryRenewAsync(first.Identity, DURATION, Token));
        Assert.Empty((await store.GetSnapshotAsync(resource.Scope, "", Token)).Leases);
        var next = Assert.IsType<LeaseGrant>(await store.TryAcquireAsync(resource, "holder", DURATION, Token));
        Assert.NotEqual(first.Identity.TermId, next.Identity.TermId);
        Assert.False(await store.TryReleaseAsync(first.Identity, Token));
        Assert.True(await store.TryReleaseAsync(next.Identity, Token));
    }

    [Fact]
    public async Task Observation_ShouldUseServerTimePreserveAcquisitionAndIsolateLiteralNamespaces()
    {
        using var connection = await ConnectAsync();
        var options = Options();
        var store = new RedisLeaseStore(connection, options);
        var otherStore = new RedisLeaseStore(connection, options with { KeyPrefix = options.KeyPrefix + ":other" });
        var resource = new LeaseResource("scope:*{a}?", "leader:服务");
        var first = Assert.IsType<LeaseGrant>(await store.TryAcquireAsync(resource, "one", DURATION, Token));
        var unrelated = Assert.IsType<LeaseGrant>(await otherStore.TryAcquireAsync(resource, "two", DURATION, Token));
        var otherScope = Assert.IsType<LeaseGrant>(await store.TryAcquireAsync(new("scope:*{a}?:other", resource.Name), "three", DURATION, Token));
        var renewed = Assert.IsType<LeaseGrant>(await store.TryRenewAsync(first.Identity, DURATION, Token));
        var snapshot = await store.GetSnapshotAsync(resource.Scope, "leader:", Token);
        var row = Assert.Single(snapshot.Leases);
        Assert.Equal(first.ConfirmedAtUtc, row.AcquiredAtUtc);
        Assert.Equal(renewed.ConfirmedAtUtc, row.ConfirmedAtUtc);
        Assert.Equal(renewed.ExpiresAtUtc, row.ExpiresAtUtc);
        Assert.True(row.ExpiresAtUtc > snapshot.ObservedAtUtc);
        Assert.Equal("one", row.HolderId);
        Assert.Empty((await store.GetSnapshotAsync(resource.Scope, "different:", Token)).Leases);
        await store.TryReleaseAsync(first.Identity, Token);
        await store.TryReleaseAsync(otherScope.Identity, Token);
        await otherStore.TryReleaseAsync(unrelated.Identity, Token);
    }

    [Fact]
    public async Task Snapshot_ShouldScanTheSelectedDatabaseRatherThanDatabaseZero()
    {
        using var connection = await ConnectAsync();
        var options = Options();
        var zero = new RedisLeaseStore(connection, options);
        var one = new RedisLeaseStore(connection, options with { Database = 1 });
        var resource = new LeaseResource("database", "role");
        var grant = Assert.IsType<LeaseGrant>(await one.TryAcquireAsync(resource, "one", DURATION, Token));
        Assert.Empty((await zero.GetSnapshotAsync(resource.Scope, "", Token)).Leases);
        Assert.Equal("one", Assert.Single((await one.GetSnapshotAsync(resource.Scope, "", Token)).Leases).HolderId);
        await one.TryReleaseAsync(grant.Identity, Token);
    }

    [Fact]
    public async Task ClosedConnection_ShouldPropagateInsteadOfBecomingContentionOrMissingLeadership()
    {
        using var connection = await ConnectAsync();
        var store = new RedisLeaseStore(connection, Options());
        var grant = Assert.IsType<LeaseGrant>(await store.TryAcquireAsync(new("failure", "role"), "holder", TimeSpan.FromSeconds(1), Token));
        await connection.CloseAsync(allowCommandsToComplete: false);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.TryRenewAsync(grant.Identity, DURATION, Token));
        var observationFailure = await Record.ExceptionAsync(() => store.GetSnapshotAsync("failure", "", Token));
        Assert.NotNull(observationFailure);
    }

    [Fact]
    public async Task KeyedRegistration_ShouldSelectIndependentProvidersAndKeepTheSharedConnectionAlive()
    {
        using var connection = await ConnectAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(connection);
        services.AddRedisLeaseStore("first", Options());
        services.AddRedisLeaseStore("second", Options());
        Assert.Throws<InvalidOperationException>(() => services.AddRedisLeaseStore("first", Options()));
        await using (var provider = services.BuildServiceProvider())
        {
            var first = provider.GetRequiredKeyedService<IExclusiveLeaseStore>("first");
            var second = provider.GetRequiredKeyedService<IExclusiveLeaseStore>("second");
            Assert.Same(first, provider.GetRequiredKeyedService<IExclusiveLeaseStore>("first"));
            Assert.NotSame(first, second);
            Assert.Null(provider.GetService<IExclusiveLeaseStore>());
            var resource = new LeaseResource("bindings", "role");
            var a = Assert.IsType<LeaseGrant>(await first.TryAcquireAsync(resource, "a", DURATION, Token));
            var b = Assert.IsType<LeaseGrant>(await second.TryAcquireAsync(resource, "b", DURATION, Token));
            await first.TryReleaseAsync(a.Identity, Token);
            await second.TryReleaseAsync(b.Identity, Token);
        }
        Assert.True(connection.IsConnected);
        await connection.GetDatabase().PingAsync();
    }

    [Fact]
    public void MissingConnection_ShouldFailResolutionWithoutAnInMemoryFallback()
    {
        var services = new ServiceCollection().AddRedisLeaseStore("missing", Options(), "absent-connection");
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredKeyedService<IExclusiveLeaseStore>("missing"));
    }

    private static RedisLeaseStoreOptions Options() => new() { KeyPrefix = "tests:" + Guid.NewGuid().ToString("N") };

    private static async Task<ConnectionMultiplexer> ConnectAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("MONICA_LEASE_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(endpoint)) Assert.Skip("Set MONICA_LEASE_TEST_REDIS to a disposable Redis authority.");
        var options = ConfigurationOptions.Parse(endpoint);
        options.AbortOnConnectFail = true;
        options.AsyncTimeout = 2000;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }
}
