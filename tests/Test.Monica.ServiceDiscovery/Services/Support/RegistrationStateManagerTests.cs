using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Locker.Leases;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Providers;
using Monica.ServiceDiscovery.Services.Support;
using Monica.StateStore.Providers.Memory;
using Xunit;

namespace Test.Monica.ServiceDiscovery.Services.Support;

public sealed class RegistrationStateManagerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RegisterOrHeartbeatAsync_WhenMetadataIsConfigured_ShouldPersistCurrentMetadataOnEveryWrite(
        bool initiallyMissing,
        bool changeEnvironmentValue)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var environmentVariable = $"MONICA_DISCOVERY_METADATA_TEST_{Guid.NewGuid():N}";
        const string initialAddresses = "http://192.0.2.10:30100;http://192.0.2.10:30101";
        const string changedAddresses = "http://192.0.2.20:30200";
        const string listeningAddress = "http://127.0.0.1:8080";
        const string registrationKey = "reg:metadata-service:metadata-instance";
        var previousValue = Environment.GetEnvironmentVariable(environmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(environmentVariable, initiallyMissing ? null : initialAddresses);
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var store = new MemoryCacheProvider(memoryCache, NullLogger<MemoryCacheProvider>.Instance);
            var options = Options.Create(new ModuleServiceDiscoveryOption
            {
                AppId = "metadata-service",
                FromInstance = "metadata-instance",
                MetadataEnvironmentVariables = [environmentVariable],
                IncludeListeningAddresses = true
            });
            var serverAddresses = new ServerAddressesFeature();
            serverAddresses.Addresses.Add(listeningAddress);
            IServiceDiscoveryClientInfo clientInfo = new DefaultServiceDiscoveryClientInfo(
                options, new MonicaApplicationOptions(), serverAddresses);
            var leases = new InMemoryLeaseStore(TimeProvider.System);
            using var leaderService = new LeaderElectionService(
                clientInfo, options, NullLogger<LeaderElectionService>.Instance, leases, TimeProvider.System);
            var manager = new RegistrationStateManager(
                store, clientInfo, leaderService, options, NullLogger<RegistrationStateManager>.Instance, leases);

            var registration = await manager.RegisterOrHeartbeatAsync(cancellationToken);

            registration.Success.Should().BeTrue();
            registration.ErrorMessage.Should().BeNull();
            var registered = await store.GetStateAsync<InstanceState>(registrationKey, cancellationToken);
            registered.Should().NotBeNull();
            if (initiallyMissing)
            {
                registered!.Metadata.Should().NotContainKey(environmentVariable);
            }
            else
            {
                registered!.Metadata.Should().Contain(environmentVariable, initialAddresses);
            }
            registered.Metadata.Should().Contain(
                IServiceDiscoveryClientInfo.LISTENING_ADDRESS_METADATA_KEY, listeningAddress);

            var expectedAddresses = changeEnvironmentValue ? changedAddresses : initialAddresses;
            Environment.SetEnvironmentVariable(environmentVariable, expectedAddresses);

            var heartbeat = await manager.RegisterOrHeartbeatAsync(cancellationToken);

            heartbeat.Success.Should().BeTrue();
            heartbeat.ErrorMessage.Should().BeNull();
            var heartbeating = await store.GetStateAsync<InstanceState>(registrationKey, cancellationToken);
            heartbeating.Should().NotBeNull();
            heartbeating!.Metadata.Should().Contain(environmentVariable, expectedAddresses);
            heartbeating.Metadata.Should().Contain(
                IServiceDiscoveryClientInfo.LISTENING_ADDRESS_METADATA_KEY, listeningAddress);
            heartbeating.RegistrationTime.Should().Be(registered.RegistrationTime);
            heartbeating.LastHeartbeatTime.Should().BeOnOrAfter(registered.LastHeartbeatTime);

            var followingHeartbeat = await manager.RegisterOrHeartbeatAsync(cancellationToken);

            followingHeartbeat.Success.Should().BeTrue();
            followingHeartbeat.ErrorMessage.Should().BeNull();
            var following = await store.GetStateAsync<InstanceState>(registrationKey, cancellationToken);
            following.Should().NotBeNull();
            following!.Metadata.Should().Contain(environmentVariable, expectedAddresses);
            following.Metadata.Should().Contain(
                IServiceDiscoveryClientInfo.LISTENING_ADDRESS_METADATA_KEY, listeningAddress);
            following.RegistrationTime.Should().Be(registered.RegistrationTime);
            following.LastHeartbeatTime.Should().BeOnOrAfter(heartbeating.LastHeartbeatTime);

            var discovered = (await manager.GetAllInstancesAsync(cancellationToken)).Should().ContainSingle().Subject;
            discovered.Metadata.Should().Contain(environmentVariable, expectedAddresses);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, previousValue);
        }
    }
}
