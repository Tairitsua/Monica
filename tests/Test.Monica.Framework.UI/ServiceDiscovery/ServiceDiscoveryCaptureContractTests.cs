using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.Core.Results;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Facades;
using Monica.ServiceDiscovery.Localization;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Services;
using Monica.StateStore.Abstractions;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Framework.UI.ServiceDiscovery;

public sealed class ServiceDiscoveryCaptureContractTests
{
    [Fact]
    public async Task CaptureServicesAsync_WhenRegistryReadFails_ShouldDistinguishFailureFromSuccessfulEmpty()
    {
        await using var application = await new ServiceDiscoveryTestApplicationFactory().CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        await StopLocalRegistrationAsync(application);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<ServiceDiscoveryFacade>();
        var manager = scope.Resolve<IRegistrationStateManager>();
        var store = scope.Resolve<ReadFailureMemoryStore>();

        var empty = await facade.CaptureServicesAsync(scope.CancellationToken);
        AssertSuccess(empty);
        empty.Data!.Should().BeEmpty();

        store.FailRegistryRead = true;
        Func<Task> read = async () => await manager.GetAllInstancesAsync(scope.CancellationToken);
        await read.Should().ThrowAsync<IOException>().WithMessage(ReadFailureMemoryStore.RegistryFailure);
        AssertSafeFailure(await facade.CaptureServicesAsync(scope.CancellationToken), ReadFailureMemoryStore.RegistryFailure,
            scope.Resolve<IStringLocalizer<ServiceDiscoveryResource>>());

        store.FailRegistryRead = false;
        var recovered = await facade.CaptureServicesAsync(scope.CancellationToken);
        AssertSuccess(recovered);
        recovered.Data!.Should().BeEmpty();
    }

    [Fact]
    public async Task CaptureCurrentInstanceAsync_WhenLeaseReadFails_ShouldDistinguishFailureFromAbsentLease()
    {
        await using var application = await new ServiceDiscoveryTestApplicationFactory().CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<ServiceDiscoveryFacade>();
        var manager = scope.Resolve<IRegistrationStateManager>();

        var absent = await facade.CaptureCurrentInstanceAsync(scope.CancellationToken);
        AssertSuccess(absent);
        absent.Data!.ClusterLeaderState.Should().BeNull();

        scope.Resolve<ReadFailureMemoryStore>().FailLeaseRead = true;
        Func<Task> read = async () => await manager.GetLeaderStateAsync(scope.CancellationToken);
        await read.Should().ThrowAsync<IOException>().WithMessage(ReadFailureMemoryStore.LeaseFailure);
        AssertSafeFailure(await facade.CaptureCurrentInstanceAsync(scope.CancellationToken), ReadFailureMemoryStore.LeaseFailure,
            scope.Resolve<IStringLocalizer<ServiceDiscoveryResource>>());
    }

    [Fact]
    public async Task GetServicesStatusAsync_WhenMappingMembership_ShouldLeaveStoredStatusAndCollectionsUnchanged()
    {
        await using var application = await new ServiceDiscoveryTestApplicationFactory().CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var store = scope.ServiceProvider.GetRequiredKeyedService<IStateStore>(nameof(ModuleServiceDiscovery));
        var source = CreateInstance();
        await store.SaveStateAsync("reg:remote-app:remote-instance", source, scope.CancellationToken, TimeSpan.FromHours(1));

        var services = await scope.Resolve<ServiceDiscoveryQueryService>().GetServicesStatusAsync();
        var service = services.Single(item => item.AppId == source.AppId);
        var captured = service.Instances[source.InstanceId];
        captured.Status.Should().Be(ServiceStatus.Running);
        service.OverallStatus.Should().Be(ServiceStatus.Running);
        source.Status.Should().Be(ServiceStatus.Error);
        (await store.GetStateAsync<InstanceState>("reg:remote-app:remote-instance", scope.CancellationToken))!
            .Status.Should().Be(ServiceStatus.Error);

        captured.Metadata["TRACE"] = "inspector edit";
        source.Metadata["trace"].Should().Be("original evidence");
    }

    [Fact]
    public async Task CaptureServicesAsync_WhenPreloadProviderReadFails_ShouldReturnFailureWithoutPartialRegistry()
    {
        var provider = new ControlledDiscoveryCatalog { FailPreloadRead = true };
        await using var application = await new ServiceDiscoveryTestApplicationFactory().CreateAsync(
            seams => seams.With<IServiceDiscoveryCatalogProvider>(provider), TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var store = scope.ServiceProvider.GetRequiredKeyedService<IStateStore>(nameof(ModuleServiceDiscovery));
        var instance = CreateInstance();
        await store.SaveStateAsync($"reg:{instance.AppId}:{instance.InstanceId}", instance,
            scope.CancellationToken, TimeSpan.FromHours(1));

        AssertSafeFailure(await scope.Resolve<ServiceDiscoveryFacade>().CaptureServicesAsync(scope.CancellationToken),
            ControlledDiscoveryCatalog.PreloadFailure, scope.Resolve<IStringLocalizer<ServiceDiscoveryResource>>());
    }

    [Theory]
    [InlineData(ServiceDiscoveryRole.Worker, false)]
    [InlineData(ServiceDiscoveryRole.Registry, true)]
    [InlineData(ServiceDiscoveryRole.Standalone, true)]
    public async Task CaptureCurrentInstanceAsync_WhenRoleIsConfigured_ShouldExposeElectionScopeAndDetachStoredLease(
        ServiceDiscoveryRole role, bool electionEnabled)
    {
        await using var application = await new ServiceDiscoveryTestApplicationFactory(role).CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        await StopLocalRegistrationAsync(application);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var lease = new LeaderState
        {
            AppId = ServiceDiscoveryTestApplicationFactory.AppId,
            InstanceId = "stored-leader",
            BecomeLeaderTime = new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc)
        };
        var store = scope.ServiceProvider.GetRequiredKeyedService<IStateStore>(nameof(ModuleServiceDiscovery));
        await store.SaveStateAsync($"leader:{lease.AppId}", lease, scope.CancellationToken, TimeSpan.FromHours(1));

        var result = await scope.Resolve<ServiceDiscoveryFacade>().CaptureCurrentInstanceAsync(scope.CancellationToken);
        AssertSuccess(result);
        var snapshot = result.Data!;
        snapshot.IsLeaderElectionEnabled.Should().Be(electionEnabled);
        snapshot.CurrentInstance.AppId.Should().Be(ServiceDiscoveryTestApplicationFactory.AppId);
        lease.InstanceId = "replacement-leader";
        lease.BecomeLeaderTime = lease.BecomeLeaderTime.AddHours(1);
        var capturedLease = snapshot.ClusterLeaderState!;
        capturedLease.InstanceId.Should().Be("stored-leader");
        capturedLease.BecomeLeaderTime.Should().Be(new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task CaptureCurrentInstanceAsync_WhenRegistrationIncludesListeningAddresses_ShouldKeepFullDetachedMetadata()
    {
        const string originalAddress = "http://discovery-contract.invalid:5320";
        const string replacementAddress = "http://replacement-contract.invalid:5321";
        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add(originalAddress);
        await using var application = await new ServiceDiscoveryTestApplicationFactory().CreateAsync(
            seams => seams.With<IServerAddressesFeature>(addresses), TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var clientInfo = scope.Resolve<IServiceDiscoveryClientInfo>();
        var facade = scope.Resolve<ServiceDiscoveryFacade>();
        var metadataKey = IServiceDiscoveryClientInfo.LISTENING_ADDRESS_METADATA_KEY;

        clientInfo.GetServiceStatus(isHeartbeatInfo: true).Metadata.Should().NotContainKey(metadataKey);
        var result = await facade.CaptureCurrentInstanceAsync(scope.CancellationToken);
        AssertSuccess(result);
        var captured = result.Data!.CurrentInstance;
        captured.Metadata[metadataKey].Should().Be(originalAddress);

        addresses.Addresses.Clear();
        addresses.Addresses.Add(replacementAddress);
        captured.Metadata[metadataKey].Should().Be(originalAddress);
        captured.Metadata[metadataKey] = "inspector-local address";

        var fresh = await facade.CaptureCurrentInstanceAsync(scope.CancellationToken);
        AssertSuccess(fresh);
        fresh.Data!.CurrentInstance.Metadata[metadataKey].Should().Be(replacementAddress);
        captured.Metadata[metadataKey].Should().Be("inspector-local address");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureServicesAsync_WhenCancellationRetiresHeldPreload_ShouldDrainProviderAndRejectItsOutcome(
        bool providerFaults)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<List<PredefinedServiceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledDiscoveryCatalog();
        provider.PreloadedServices.Add(new PredefinedServiceInfo { AppId = "predefined-app", AppName = "Predefined service" });
        provider.ReadPreloadedServices = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        await using var application = await new ServiceDiscoveryTestApplicationFactory().CreateAsync(
            seams => seams.With<IServiceDiscoveryCatalogProvider>(provider), TestContext.Current.CancellationToken);
        await StopLocalRegistrationAsync(application);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<ServiceDiscoveryFacade>();
        var pending = facade.CaptureServicesAsync(cancellation.Token);

        try
        {
            await entered.Task.WaitAsync(scope.CancellationToken);
            cancellation.Cancel();
            pending.IsCompleted.Should().BeFalse("the tokenless preload read must drain before cancellation is observed");
        }
        finally
        {
            if (providerFaults)
            {
                release.TrySetException(new IOException(ControlledDiscoveryCatalog.PreloadFailure));
            }
            else
            {
                release.TrySetResult(provider.PreloadedServices);
            }
        }

        Func<Task> capture = async () => await pending;
        await capture.Should().ThrowAsync<OperationCanceledException>();
        release.Task.IsCompleted.Should().BeTrue();
        provider.ReadPreloadedServices = null;

        var fresh = await facade.CaptureServicesAsync(scope.CancellationToken);
        AssertSuccess(fresh);
        var service = fresh.Data.Should().ContainSingle().Which;
        service.AppId.Should().Be("predefined-app");
        service.AppName.Should().Be("Predefined service");
        service.Instances.Should().BeEmpty();
    }

    internal static async Task StopLocalRegistrationAsync(MonicaTestApplication application)
    {
        await application.Services.GetRequiredService<ServiceDiscoveryClientHostedService>()
            .StopAsync(TestContext.Current.CancellationToken);
        var store = application.Services.GetRequiredKeyedService<IStateStore>(nameof(ModuleServiceDiscovery));
        await store.DeleteBulkStateAsync(await store.ScanKeysAsync("reg:*", TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await store.DeleteBulkStateAsync(await store.ScanKeysAsync("leader:*", TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
    }

    private static void AssertSuccess<T>(Res<T> result) where T : class
    {
        result.Status.Should().Be(ResStatus.Ok);
        result.Message.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();
    }

    private static void AssertSafeFailure<T>(Res<T> result, string providerMessage,
        IStringLocalizer<ServiceDiscoveryResource> localizer) where T : class
    {
        result.Status.Should().Be(ResStatus.BadRequest);
        result.Message.Should().Be(localizer["Workbench:ReadFailed"].Value);
        result.Message.Should().NotContain(providerMessage);
        result.Data.Should().BeNull();
    }

    internal static InstanceState CreateInstance() => new()
    {
        AppId = "remote-app",
        AppName = "Remote service",
        InstanceId = "remote-instance",
        ProjectName = "Operations",
        DomainName = "Dispatch",
        Status = ServiceStatus.Error,
        IsLeader = true,
        BuildTime = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
        AssemblyVersion = "1.2.3",
        ReleaseVersion = "release-1",
        RegistrationTime = new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc),
        LastHeartbeatTime = new DateTime(2026, 10, 9, 2, 0, 0, DateTimeKind.Utc),
        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["trace"] = "original evidence" }
    };
}
