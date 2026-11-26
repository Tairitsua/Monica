using AwesomeAssertions;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.EventBus.Events;
using Monica.Modules;
using Monica.ApplicationModel.Annotations;
using Monica.BuildingBlocks.Facades;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.BuildingBlocks.Facades;

public sealed class BuildingBlocksFacadeTests
{
    [Fact]
    public async Task GetDomainEvents_WhenDisplayTitleDiffers_ShouldRetainFullTypeIdentity()
    {
        await using var application = await new FacadeScenarioFactory()
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<BuildingBlocksFacade>();
        var descriptors = await facade.GetDomainEventsAsync();
        descriptors.Status.Should().Be(ResultStatus.Ok);
        var descriptor = descriptors.Data!.Single(item => item.Info.Key == typeof(EventFriendlyDispatch).FullName);
        descriptor.Info.Title.Should().Be("Friendly dispatch label");
        descriptor.EventKey.Should().Be(typeof(EventFriendlyDispatch).FullName);
    }

    [Fact]
    public async Task GetDomainEvents_WhenRecordRequiresConstructorArguments_ShouldRetainDescriptorWithoutSample()
    {
        await using var application = await new FacadeScenarioFactory()
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<BuildingBlocksFacade>().GetDomainEventsAsync();

        result.Status.Should().Be(ResultStatus.Ok);
        result.Data!.Single(item => item.EventKey == typeof(EventParameterized).FullName).Structure.Should().BeNull();
        result.Data!.Single(item => item.EventKey == typeof(EventFriendlyDispatch).FullName).Structure.Should().NotBeNull();
    }

    public sealed record EventParameterized(string Value) : IDomainEvent;

    [BuildingBlockMetadata("Friendly dispatch label")]
    public sealed class EventFriendlyDispatch : IDomainEvent
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class FacadeScenarioFactory
        : MonicaTestApplicationFactory<EventFriendlyDispatch>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options => options.AutoAddMonicaHttpListener = false);
            builder.AddBuildingBlocks();
        }
    }
}
