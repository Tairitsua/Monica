using Monica.Locker.Leases;
using Monica.Locker.Redis;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Test.Monica.Locker.Redis;

public sealed class RedisLeaseObservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetSnapshotAsync_WhenReadingOneResource_ShouldUseOneKeyAndReturnAuthorityTime(bool exists)
    {
        var resource = new LeaseResource("scope", "leader");
        var database = Substitute.For<IDatabase>();
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase(0).Returns(database);
        RedisValue[] response = exists ? [1000L, resource.Name, "holder", 500L, 800L, 200L] : [1000L];
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), flags: CommandFlags.DemandMaster)
            .Returns(RedisResult.Create(response));
        var store = new RedisLeaseStore(connection, new RedisLeaseStoreOptions { KeyPrefix = "test" });

        var snapshot = await store.GetSnapshotAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1000), snapshot.ObservedAtUtc);
        if (exists)
        {
            var row = Assert.Single(snapshot.Leases);
            Assert.Equal(resource, row.Resource);
            Assert.Equal("holder", row.HolderId);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(500), row.AcquiredAtUtc);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(800), row.ConfirmedAtUtc);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1200), row.ExpiresAtUtc);
        }
        else Assert.Empty(snapshot.Leases);
        await database.Received(1).ScriptEvaluateAsync(Arg.Any<string>(), Arg.Is<RedisKey[]>(keys => keys.Length == 1),
            flags: CommandFlags.DemandMaster);
        connection.DidNotReceiveWithAnyArgs().GetEndPoints();
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenCancelled_ShouldNotSendAnObservationCommand()
    {
        var database = Substitute.For<IDatabase>();
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase(0).Returns(database);
        var store = new RedisLeaseStore(connection, new RedisLeaseStoreOptions { KeyPrefix = "test" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.GetSnapshotAsync(new LeaseResource("scope", "leader"), new CancellationToken(canceled: true)));

        await database.DidNotReceiveWithAnyArgs().ScriptEvaluateAsync(default(string)!, default(RedisKey[]), default(RedisValue[]));
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenRedisFails_ShouldPropagateInsteadOfReportingAnAbsentLease()
    {
        var database = Substitute.For<IDatabase>();
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase(0).Returns(database);
        var failure = new RedisConnectionException(ConnectionFailureType.UnableToResolvePhysicalConnection, "Unavailable authority");
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), flags: CommandFlags.DemandMaster)
            .Returns(Task.FromException<RedisResult>(failure));
        var store = new RedisLeaseStore(connection, new RedisLeaseStoreOptions { KeyPrefix = "test" });

        var exception = await Assert.ThrowsAsync<RedisConnectionException>(() =>
            store.GetSnapshotAsync(new LeaseResource("scope", "leader"), TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
    }
}
