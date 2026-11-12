using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Locker.Leases;
using Monica.Locker.Redis;
using StackExchange.Redis;
using Test.Monica.Locker.Redis.Support;
using Xunit;

namespace Test.Monica.Locker.Redis;

[Trait("Category", "ExternalRedisSentinel")]
public sealed class RedisSentinelLeaseTests
{
    [Fact]
    public async Task Coordinator_WhenSentinelPromotesAfterPrimaryFailure_ShouldRecoverRenewalAndFencePreviousTerms()
    {
        if (Environment.GetEnvironmentVariable("MONICA_LEASE_TEST_SENTINEL") != "1")
            Assert.Skip("Set MONICA_LEASE_TEST_SENTINEL=1 to create and destroy an isolated Docker Sentinel topology.");
        var output = Environment.GetEnvironmentVariable("MONICA_REDIS_SENTINEL_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output)
            && (!Path.IsPathFullyQualified(output) || !Directory.Exists(Path.GetDirectoryName(output))))
            throw new InvalidOperationException("MONICA_REDIS_SENTINEL_OUTPUT must name a file in an existing absolute directory.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        await using var sandbox = await RedisSentinelSandbox.StartAsync(token);
        using var discovery = await ConnectionMultiplexer.SentinelConnectAsync(sandbox.SentinelOptions());
        var sentinel = discovery.GetServer(IPAddress.Loopback.ToString(), sandbox.SentinelPorts[0]);
        var originalPrimary = await WaitAsync(async () => await sentinel.SentinelGetMasterAddressByNameAsync(sandbox.ServiceName), "initial primary", token);
        using var connection = discovery.GetSentinelMasterConnection(sandbox.DataOptions());
        using var secondDiscovery = await ConnectionMultiplexer.SentinelConnectAsync(sandbox.SentinelOptions());
        using var secondConnection = secondDiscovery.GetSentinelMasterConnection(sandbox.DataOptions());
        var storeOptions = new RedisLeaseStoreOptions { KeyPrefix = sandbox.ServiceName };
        var store = new RedisLeaseStore(connection, storeOptions);
        var otherStore = new RedisLeaseStore(secondConnection, storeOptions);
        var options = new LeaseCoordinatorOptions
        {
            Duration = TimeSpan.FromSeconds(6), OperationTimeout = TimeSpan.FromMilliseconds(500),
            StandbyInterval = TimeSpan.FromMilliseconds(100)
        };
        var resource = new LeaseResource("sentinel-recovery", "leader");
        await WaitAsync(async () =>
        {
            var replicas = await sentinel.SentinelGetReplicaAddressesAsync(sandbox.ServiceName);
            var peers = await sentinel.SentinelGetSentinelAddressesAsync(sandbox.ServiceName);
            return replicas.Length == 2 && peers.Length >= 2 ? new object() : null;
        }, "two replicas and two Sentinel peers", token);
        using var coordinator = new LeaseCoordinator(store, "initial", TimeProvider.System, NullLogger.Instance, options);
        var initial = await WaitAsync(() => coordinator.GetAsync(resource, token), "initial lease", token);
        // WAIT establishes test readiness on both replicas; it is not a claim of strong consistency on failover.
        await WaitAsync(async () => (long)await connection.GetDatabase().ExecuteAsync("WAIT", 2, 500) == 2 ? new object() : null,
            "initial lease replication", token);
        var before = Assert.Single((await store.GetSnapshotAsync(resource.Scope, "", token)).Leases);
        var failureStarted = Stopwatch.GetTimestamp();
        await sandbox.StopPrimaryAsync(originalPrimary, token);
        var promoted = await WaitAsync(async () =>
        {
            var endpoint = await sentinel.SentinelGetMasterAddressByNameAsync(sandbox.ServiceName);
            return endpoint is not null && !endpoint.Equals(originalPrimary) ? endpoint : null;
        }, "different primary after SIGKILL", token);
        var promotedSnapshot = await WaitAsync(async () => await store.GetSnapshotAsync(resource.Scope, "", token), "post-promotion snapshot", token);
        var confirmationAtPromotion = promotedSnapshot.Leases.SingleOrDefault()?.ConfirmedAtUtc ?? before.ConfirmedAtUtc;

        // A fast failover can preserve the term; an ambiguous/slow renewal must retire it permanently.
        // Both are valid. Do not manufacture a failure or assert global exclusivity across Redis replication.
        LeaseObservation? after = null;
        var recovered = await WaitAsync(async () =>
        {
            var context = await coordinator.GetAsync(resource, token);
            if (context is null) return null;
            after = (await store.GetSnapshotAsync(resource.Scope, "", token)).Leases.SingleOrDefault();
            return after is { HolderId: "initial" } && after.ConfirmedAtUtc > confirmationAtPromotion
                && after.ConfirmedAtUtc > after.AcquiredAtUtc
                && !context.StoppingToken.IsCancellationRequested ? context : null;
        }, "confirmed renewal after promotion", token);
        var recoveryMs = Stopwatch.GetElapsedTime(failureStarted).TotalMilliseconds;
        var retiredDuringFailover = initial.StoppingToken.IsCancellationRequested;
        if (retiredDuringFailover) Assert.NotEqual(initial.Identity.TermId, recovered.Identity.TermId);

        // Let the old role expire after the caller drains. A successor must get a distinct term on the new primary.
        coordinator.Dispose();
        Assert.True(recovered.StoppingToken.IsCancellationRequested);
        await using var successor = new LeaseCoordinator(otherStore, "successor", TimeProvider.System, NullLogger.Instance, options);
        var current = await WaitAsync(() => successor.GetAsync(resource, token), "successor lease after previous TTL", token);
        Assert.NotEqual(recovered.Identity.TermId, current.Identity.TermId);
        foreach (var identity in new[] { initial.Identity, recovered.Identity }.Distinct())
        {
            Assert.Null(await otherStore.TryRenewAsync(identity, options.Duration, token));
            Assert.False(await otherStore.TryReleaseAsync(identity, token));
        }
        Assert.True(initial.StoppingToken.IsCancellationRequested);
        Assert.True(recovered.StoppingToken.IsCancellationRequested);
        Assert.False(current.StoppingToken.IsCancellationRequested);
        Assert.Equal(current.Identity, (await otherStore.TryRenewAsync(current.Identity, options.Duration, token))!.Identity);
        await successor.DisposeAsync();
        Assert.Empty((await otherStore.GetSnapshotAsync(resource.Scope, "", token)).Leases);
        await sandbox.DisposeAsync();
        Assert.True(sandbox.Removed);

        if (!string.IsNullOrWhiteSpace(output))
        {
            var report = new
            {
                CapturedAtUtc = DateTimeOffset.UtcNow, sandbox.ImageId, OriginalPrimary = originalPrimary.ToString(),
                PromotedPrimary = promoted.ToString(), RecoveryMs = recoveryMs, RetiredDuringFailover = retiredDuringFailover,
                RenewedAfterPromotion = after!.ConfirmedAtUtc > confirmationAtPromotion,
                FreshSuccessorTerm = true, StaleRenewRejected = true, StaleReleaseRejected = true,
                RetiredTokensStayedCancelled = true, TemporaryLeasesRemaining = 0, ContainerRemoved = sandbox.Removed,
                Scope = "Owned Docker topology only; no guarantee of global exclusivity across asynchronous replication."
            };
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
        }
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> read, string condition, CancellationToken token) where T : class
    {
        Exception? lastFailure = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (await read() is { } value) return value;
                }
                catch (Exception exception) when (exception is RedisConnectionException
                    || exception is RedisTimeoutException || exception is TimeoutException
                    || exception is RedisServerException && (exception.Message.StartsWith("LOADING ", StringComparison.Ordinal)
                        || exception.Message.StartsWith("READONLY ", StringComparison.Ordinal)))
                {
                    lastFailure = exception;
                }
                // Native Sentinel failure detection/reconfiguration uses real elapsed time.
                await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            }
        }
        catch (OperationCanceledException exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException($"Stopped waiting for {condition}.", lastFailure ?? exception, token);
        }
    }
}
