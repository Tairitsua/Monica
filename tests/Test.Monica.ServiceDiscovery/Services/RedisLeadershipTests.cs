using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Abstractions;
using Monica.Locker.Redis;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.StateStore.Abstractions;
using Monica.Testing.Hosting;
using StackExchange.Redis;
using Xunit;

namespace Test.Monica.ServiceDiscovery.Services;

[Trait("Category", "ExternalRedis")]
public sealed class RedisLeadershipTests
{
    [Fact]
    public async Task TwoRealHosts_ShouldShareTheKeyedAuthorityAndReadLeadershipOutsideMembershipState()
    {
        var endpoint = Environment.GetEnvironmentVariable("MONICA_LEASE_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(endpoint)) Assert.Skip("Set MONICA_LEASE_TEST_REDIS to test native discovery election.");
        var token = TestContext.Current.CancellationToken;
        using var connection = await ConnectionMultiplexer.ConnectAsync(endpoint);
        var scope = "discovery-test:" + Guid.NewGuid().ToString("N");
        await using var first = await new Factory(connection, scope, "first").CreateAsync(cancellationToken: token);
        await using var second = await new Factory(connection, scope, "second").CreateAsync(cancellationToken: token);
        var a = first.Services.GetRequiredService<ILeaderElectionService>();
        var b = second.Services.GetRequiredService<ILeaderElectionService>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!a.IsLeader && !b.IsLeader) await Task.Delay(20, deadline.Token);
        Assert.NotEqual(a.IsLeader, b.IsLeader);
        var term = (a.CurrentLease ?? b.CurrentLease)!;
        Assert.Equal(scope, term.Identity.Resource.Scope);
        Assert.NotNull(term.Identity.TermId);
        var firstState = await first.Services.GetRequiredService<IRegistrationStateManager>().GetLeaderStateAsync(token);
        var secondState = await second.Services.GetRequiredService<IRegistrationStateManager>().GetLeaderStateAsync(token);
        Assert.Equal(term.Identity.HolderId, firstState!.InstanceId);
        Assert.Equal(firstState.InstanceId, secondState!.InstanceId);
        Assert.Equal(firstState.BecomeLeaderTime, secondState.BecomeLeaderTime);
        Assert.False(await first.Services.GetRequiredKeyedService<IStateStore>(nameof(ModuleServiceDiscovery))
            .ExistAsync("leader:app", token));
        Assert.False(await second.Services.GetRequiredKeyedService<IStateStore>(nameof(ModuleServiceDiscovery))
            .ExistAsync("leader:app", token));
    }

    private sealed class Factory(IConnectionMultiplexer connection, string scope, string instance)
        : MonicaTestApplicationFactory<RedisLeadershipTests>
    {
        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            builder.Services.AddSingleton(connection);
            builder.Services.AddRedisLeaseStore("coordination", new RedisLeaseStoreOptions { KeyPrefix = scope });
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => options.AppId = "app");
            builder.AddServiceDiscovery(options =>
            {
                options.FromInstance = instance;
                options.Election.LeaseStandbyInterval = TimeSpan.FromMilliseconds(20);
            }).AsRegistry().UseMemoryStorage().UseLeaderLeaseStore("coordination", scope);
        }
    }
}
