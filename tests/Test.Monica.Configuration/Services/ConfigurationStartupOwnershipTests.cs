using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Modules;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class ConfigurationStartupOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareMutation_WhenOwnerIsStartup_ShouldRejectBothTargetsBeforeInspectingSources(bool external)
    {
        var definition = TestConfigurationFactory.Definition() with { Kind = ConfigurationKind.Startup };
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(definition);
        var sources = Substitute.For<IConfigurationSourceInspector>();
        var planner = new ConfigurationMutationPlanner(new ConfigurationDefinitionResolver(registry),
            ConfigurationValidationTestServices.CreateCoordinator(), new ConfigurationPathProjector(), sources);
        var command = new ConfigurationMutationCommand
        {
            RequestId = "startup-mutation", DefinitionKey = definition.DefinitionKey,
            LogicalPath = LogicalPath.Root, Value = ConfigurationStoredValue.FromJson("{}"),
            Target = external ? new ConfigurationExternalSourceMutationTarget { SourceKey = "json:deployment" }
                : new ConfigurationEffectiveStoreMutationTarget()
        };

        var mutate = () => planner.PrepareAsync(command, new ConfigurationMutationContext(), TestContext.Current.CancellationToken);

        await mutate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Startup*deployment-owned*");
        sources.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task CaptureUnifiedVersion_WhenFilterIncludesStartup_ShouldSkipItWithoutReadingEffectiveStore()
    {
        var definition = TestConfigurationFactory.Definition() with { Kind = ConfigurationKind.Startup };
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(definition);
        var values = Substitute.For<IConfigurationEffectiveValueStore>();
        var context = new ConfigurationRuntimeContext();
        context.Capture(new ConfigurationBuilder().Build());
        var snapshots = new ConfigurationEffectiveSnapshotReader(values,
            ConfigurationValidationTestServices.CreateSeedFactory(context),
            new ConfigurationProviderReloadCoordinator(context, new MonicaConfigurationProviderAccessor(), new ConfigurationRuntimeSnapshotLock()));
        var filter = Substitute.For<IConfigurationUnifiedVersionFilter>();
        filter.ShouldInclude(Arg.Any<ConfigurationDefinition>()).Returns(true);
        var factory = new ConfigurationUnifiedVersionSnapshotFactory(new ConfigurationDefinitionResolver(registry), snapshots,
            Substitute.For<IConfigurationSourceInspector>(), context, [filter],
            Options.Create(new ModuleConfigurationOption { UnifiedVersionControl = { Enabled = true } }));

        var request = await factory.CreateRequestAsync([definition.DefinitionKey], null, new ConfigurationMutationContext(),
            DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        request.Should().BeNull();
        values.ReceivedCalls().Should().BeEmpty();
        filter.DidNotReceive().ShouldInclude(Arg.Any<ConfigurationDefinition>());
    }

    [Fact]
    public async Task QueryHistory_WhenKeyIsNowStartup_ShouldRejectScopedHistoryButPreserveGlobalAudit()
    {
        var definition = TestConfigurationFactory.Definition() with { Kind = ConfigurationKind.Startup };
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(definition);
        var history = Substitute.For<IConfigurationHistoryStore>();
        var service = new ConfigurationHistoryService(history, registry);
        var scoped = () => service.GetHistoryAsync(definition.DefinitionKey, LogicalPath.Root, TestContext.Current.CancellationToken);

        await scoped.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Startup*global audit*");
        history.ReceivedCalls().Should().BeEmpty();
        await service.QueryHistoryAsync(null, null, null, null, null, TestContext.Current.CancellationToken);
        await history.Received(1).QueryHistoryAsync(null, null, null, null, null, TestContext.Current.CancellationToken);
    }
}
