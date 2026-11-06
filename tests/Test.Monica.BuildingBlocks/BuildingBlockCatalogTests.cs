using System.Text.Json;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Annotations;
using Monica.Configuration.Models;
using Monica.Core;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Results;
using Monica.Core.Mediator;
using Monica.Core.TypeDiscovery.Models;
using Monica.Core.XmlDocumentation.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Events;
using Monica.Framework.Seeder.Abstractions;
using Monica.Modules;
using Monica.BuildingBlocks.Abstractions;
using Monica.BuildingBlocks.Annotations;
using Monica.BuildingBlocks.Facades;
using Monica.BuildingBlocks.Models;
using Monica.BuildingBlocks.Services;
using Monica.BuildingBlocks.Services.Support;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Monica.WebApi.AutoControllers.Abstractions;
using NSubstitute;
using Xunit;

namespace Test.Monica.BuildingBlocks;

public sealed class BuildingBlockCatalogTests
{
    [Theory]
    [InlineData(typeof(OptionsManagedNaming), "issued-managed-options")]
    [InlineData(typeof(OptionsStartupNaming), "issued-startup-options")]
    public void ConfigurationNaming_WhenPrefixed_ShouldPassGlobalStrictAndPreserveDefinitionIdentity(Type type, string definitionKey)
    {
        var catalog = CreateNamingCatalog(type, BuildingBlockNamingMode.Strict);

        var unit = Assert.IsType<ConfigurationBuildingBlock>(catalog.FindByFullName(type.FullName));
        Assert.Equal(definitionKey, unit.DefinitionKey);
        Assert.DoesNotContain(unit.Alerts, alert => alert.Source == "NamingConvention");
    }

    [Theory]
    [InlineData(typeof(LegacyNamingOptions), "Prefix: Options")]
    [InlineData(typeof(DependencyDomainService), "Prefix: Domain")]
    public void DefaultNamingRule_WhenGlobalStrict_ShouldRejectInvalidName(Type type, string expectedRule)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => CreateNamingCatalog(type, BuildingBlockNamingMode.Strict));

        Assert.Contains(expectedRule, exception.Message);
    }

    [Fact]
    public void ConfigurationNaming_WhenGlobalWarning_ShouldRetainUnitWithPrefixDiagnostic()
    {
        var catalog = CreateNamingCatalog(typeof(LegacyNamingOptions), BuildingBlockNamingMode.Warning);

        var unit = Assert.IsType<ConfigurationBuildingBlock>(catalog.FindByFullName(typeof(LegacyNamingOptions).FullName));
        var alert = Assert.Single(unit.Alerts, item => item.Source == "NamingConvention");
        Assert.Equal(EAlertLevel.Warning, alert.Level);
        Assert.Contains("Prefix: Options", alert.Message);
    }

    [Fact]
    public void ConfigurationNaming_WhenCategoryOverridesGlobalMode_ShouldUseExplicitRule()
    {
        var rule = new BuildingBlockNamingRule { Prefix = "Options", Mode = BuildingBlockNamingMode.Warning };
        var catalog = CreateNamingCatalog(typeof(LegacyNamingOptions), BuildingBlockNamingMode.Strict, rule);

        var unit = Assert.IsType<ConfigurationBuildingBlock>(catalog.FindByFullName(typeof(LegacyNamingOptions).FullName));
        Assert.Equal(EAlertLevel.Warning, Assert.Single(unit.Alerts, alert => alert.Source == "NamingConvention").Level);

        rule = new BuildingBlockNamingRule { Suffix = "Options" };
        var overridden = CreateNamingCatalog(typeof(LegacyNamingOptions), BuildingBlockNamingMode.Strict, rule);
        Assert.DoesNotContain(overridden.GetAll().Single().Alerts, alert => alert.Source == "NamingConvention");
        Assert.Throws<InvalidOperationException>(() => CreateNamingCatalog(typeof(OptionsManagedNaming), BuildingBlockNamingMode.Strict, rule));
    }

    [Theory]
    [InlineData(false, BuildingBlockNamingMode.Strict)]
    [InlineData(true, BuildingBlockNamingMode.Disable)]
    public void ConfigurationNaming_WhenValidationDisabled_ShouldRetainLegacyNameWithoutAlert(bool enabled, BuildingBlockNamingMode mode)
    {
        var catalog = CreateNamingCatalog(typeof(LegacyNamingOptions), mode, enabled: enabled);

        Assert.DoesNotContain(catalog.GetAll().Single().Alerts, alert => alert.Source == "NamingConvention");
    }

    [Fact]
    public void Discovery_WhenHandlersCloseDiscoveredGenericEvents_ShouldLinkEachExactPayloadOnce()
    {
        var catalog = CreateCatalog(
            typeof(EventChanged<>), typeof(DistributedEventHandlerTextChanged),
            typeof(LocalEventHandlerTextChanged), typeof(DistributedEventHandlerNumberChanged));

        var textEvent = catalog.FindByFullName<DomainEventBuildingBlock>(typeof(EventChanged<string>).FullName)!;
        var numberEvent = catalog.FindByFullName<DomainEventBuildingBlock>(typeof(EventChanged<int>).FullName)!;
        catalog.GetAll<DomainEventBuildingBlock>().Should().HaveCount(2);
        catalog.FindByFullName(typeof(EventChanged<>).FullName).Should().BeNull();
        textEvent.ClrType.Should().Be(typeof(EventChanged<string>));
        textEvent.MetadataTitle.Should().Be("Changed payload");
        numberEvent.ClrType.Should().Be(typeof(EventChanged<int>));
        catalog.FindByName(typeof(EventChanged<>).Name).Should().BeNull();

        foreach (var handlerType in new[] { typeof(DistributedEventHandlerTextChanged), typeof(LocalEventHandlerTextChanged) })
        {
            var handler = catalog.FindByFullName(handlerType.FullName)!;
            handler.Dependencies.Should().Contain(textEvent);
            handler.Alerts.Should().NotContain(alert => alert.Source == "EventTypeAssociation");
        }

        catalog.FindByFullName(typeof(DistributedEventHandlerNumberChanged).FullName)!
            .Dependencies.Should().Contain(numberEvent);
    }

    [Fact]
    public void Discovery_WhenGenericEventDefinitionIsOutsideDiscovery_ShouldRetainAssociationWarning()
    {
        var catalog = CreateCatalog(typeof(DistributedEventHandlerTextChanged));

        catalog.GetAll<DomainEventBuildingBlock>().Should().BeEmpty();
        catalog.FindByFullName(typeof(DistributedEventHandlerTextChanged).FullName)!
            .Alerts.Should().ContainSingle(alert => alert.Source == "EventTypeAssociation");
    }

    [Fact]
    public void Discovery_WhenHandlerUsesNativeRequest_ShouldLinkItsRequestContract()
    {
        var catalog = CreateCatalog(typeof(QueryDownload), typeof(QueryHandlerDownload));
        var request = catalog.FindByFullName<RequestDtoBuildingBlock>(typeof(QueryDownload).FullName);
        var service = catalog.FindByFullName<ApplicationRequestHandlerBuildingBlock>(typeof(QueryHandlerDownload).FullName)!;

        request.Should().NotBeNull();
        service.Dependencies.Should().Contain(request!);
        service.Alerts.Should().NotContain(alert => alert.Source == "RequestTypeAssociation");
        service.ResponseType.Should().Be(typeof(byte[]));
    }

    [Theory]
    [InlineData(typeof(QueryHandlerDownload), typeof(QueryDownload), typeof(byte[]))]
    [InlineData(typeof(QueryHandlerReadText), typeof(QueryReadText), typeof(Result<string>))]
    [InlineData(typeof(CommandHandlerApply), typeof(CommandApply), typeof(Result))]
    [InlineData(typeof(QueryHandlerReadEnvelope), typeof(QueryReadEnvelope), typeof(CustomResponse))]
    public void Discovery_WhenHandlerDeclaresResponse_ShouldRetainExactMediatorContract(
        Type handlerType, Type requestType, Type responseType)
    {
        var catalog = CreateCatalog(handlerType, requestType);

        var handler = catalog.GetAll<ApplicationRequestHandlerBuildingBlock>().Should().ContainSingle().Which;
        handler.Kind.Should().Be(BuildingBlockKind.ApplicationRequestHandler);
        handler.RequestType.Should().Be(requestType);
        handler.ResponseType.Should().Be(responseType);
        handler.ExecutionPoints.Should().Equal(MediatorExecutionPoints.Request.Value);
        handler.Dependencies.Should().ContainSingle(unit => unit.ClrType == requestType);
    }

    [Fact]
    public void Discovery_WhenOnlyApplicationInfrastructureBaseIsPresent_ShouldNotClaimRequestExecution()
    {
        var catalog = CreateCatalog(typeof(CommonApplicationInfrastructure), typeof(PriorityCrudApplicationService));

        catalog.FindByFullName(typeof(CommonApplicationInfrastructure).FullName).Should().BeNull();
        catalog.GetAll<ApplicationRequestHandlerBuildingBlock>().Should().BeEmpty();
        catalog.GetAll<CrudApplicationServiceBuildingBlock>().Should().ContainSingle();
    }

    [Theory]
    [InlineData(typeof(DistributedEventHandlerSingleChanged), "eventbus.distributed-handler")]
    [InlineData(typeof(DistributedEventHandlerBatchChanged), "eventbus.distributed-batch-handler")]
    public void Discovery_WhenDistributedHandlerUsesSharedPrefix_ShouldAcceptSingleAndBatchConsumers(
        Type handlerType, string executionPoint)
    {
        var catalog = CreateNamingCatalog(handlerType, BuildingBlockNamingMode.Strict);

        var handler = catalog.GetAll<DistributedEventHandlerBuildingBlock>().Should().ContainSingle().Which;
        handler.EventType.Should().Be(typeof(EventChanged<string>));
        handler.Kind.Should().Be(BuildingBlockKind.DistributedEventHandler);
        handler.ExecutionPoints.Should().Equal(executionPoint);
        handler.Alerts.Should().NotContain(alert => alert.Source == "NamingConvention");
    }

    [Fact]
    public void GetDomainEvents_WhenClosedPayloadHasRecursiveProperties_ShouldTerminateItsSampleGraph()
    {
        var catalog = CreateCatalog(typeof(EventChanged<>), typeof(DistributedEventHandlerNodeChanged));

        var descriptor = CreateService(catalog).GetDomainEvents().Should().ContainSingle().Which;

        var payload = descriptor.Structure.Should().BeOfType<EventChanged<HierarchyNode>>().Which;
        payload.Value.Should().NotBeNull();
        payload.Value!.Parent.Should().BeNull();
    }

    [Fact]
    public void Discovery_normalizes_explicit_metadata_and_requirement_identifiers()
    {
        var catalog = CreateCatalog(typeof(AnnotatedRequest), typeof(InheritedMetadataRequest));

        var unit = catalog.FindByFullName(typeof(AnnotatedRequest).FullName);

        unit.Should().NotBeNull();
        unit!.HasExplicitMetadata.Should().BeTrue();
        unit.MetadataTitle.Should().Be("Approve Order");
        unit.Title.Should().Be("Approve Order");
        unit.Owner.Should().Be("Ordering Team");
        unit.Tags.Should().Equal("ordering", "approval");
        unit.RequirementIds.Should().Equal("ord-req-001", "ORD-REQ-002");
        unit.Alerts.Should().BeEmpty();

        var inheritedUnit = catalog.FindByFullName(typeof(InheritedMetadataRequest).FullName);
        inheritedUnit.Should().NotBeNull();
        inheritedUnit!.HasExplicitMetadata.Should().BeFalse();
        inheritedUnit.MetadataTitle.Should().BeNull();
    }

    [Fact]
    public void Discovery_records_warnings_for_malformed_explicit_annotations()
    {
        var catalog = CreateCatalog(typeof(MalformedRequest));

        var unit = catalog.FindByFullName(typeof(MalformedRequest).FullName);

        unit.Should().NotBeNull();
        unit!.HasExplicitMetadata.Should().BeTrue();
        unit.MetadataTitle.Should().BeNull();
        unit.Title.Should().Be(nameof(MalformedRequest));
        unit.Tags.Should().Equal("valid");
        unit.RequirementIds.Should().BeEmpty();
        unit.Alerts.Select(alert => alert.Source).Should().BeEquivalentTo(
            "BuildingBlock.Metadata.Title.Empty",
            "BuildingBlock.Metadata.Tag.Empty",
            "BuildingBlock.Requirement.Id.Empty");
    }

    [Fact]
    public void Discovery_preserves_specific_category_priority_for_multi_role_types()
    {
        var catalog = CreateCatalog(
            typeof(PriorityCrudApplicationService),
            typeof(PrioritySeederHostedService));

        catalog.GetAll().Should().HaveCount(2);
        catalog.FindByFullName(typeof(PriorityCrudApplicationService).FullName)
            .Should().BeOfType<CrudApplicationServiceBuildingBlock>();
        catalog.FindByFullName(typeof(PrioritySeederHostedService).FullName)
            .Should().BeOfType<SeederBuildingBlock>();
    }

    [Fact]
    public void Dashboard_reports_independent_coverage_and_dependency_health()
    {
        var documentation = Substitute.For<IXmlDocumentationService>();
        documentation.GetTypeDocumentation(typeof(DependencyDomainService)).Returns("Dependency documentation.");
        var catalog = CreateCatalog(
            documentation,
            typeof(CompleteDomainService),
            typeof(DependencyDomainService));
        var service = CreateService(catalog);

        var dashboard = service.GetDashboard();

        dashboard.TotalBuildingBlocks.Should().Be(2);
        dashboard.DistinctKinds.Should().Be(1);
        Metric(dashboard, BuildingBlockCoverageKind.Metadata).Percentage.Should().Be(50m);
        Metric(dashboard, BuildingBlockCoverageKind.Description).Percentage.Should().Be(100m);
        Metric(dashboard, BuildingBlockCoverageKind.Ownership).Percentage.Should().Be(50m);
        Metric(dashboard, BuildingBlockCoverageKind.Requirements).Percentage.Should().Be(50m);
        dashboard.Dependencies.EdgeCount.Should().Be(1);
        dashboard.Dependencies.IsolatedBuildingBlockCount.Should().Be(0);
        dashboard.CoverageGaps.Should().ContainSingle(gap =>
            gap.BuildingBlock.Key == typeof(DependencyDomainService).FullName
            && gap.MissingCoverage.Contains(BuildingBlockCoverageKind.Metadata)
            && gap.MissingCoverage.Contains(BuildingBlockCoverageKind.Ownership)
            && gap.MissingCoverage.Contains(BuildingBlockCoverageKind.Requirements));
    }

    [Fact]
    public void Description_ShouldPreserveSourcesAndApplyMetadataConfigurationXmlPrecedence()
    {
        var documentation = Substitute.For<IXmlDocumentationService>();
        documentation.GetTypeDocumentation(typeof(AllDescriptionSourcesOptions)).Returns("XML all.");
        documentation.GetTypeDocumentation(typeof(ConfigurationDescriptionOptions)).Returns("XML configuration.");
        documentation.GetTypeDocumentation(typeof(XmlDescriptionOptions)).Returns("XML only.");

        var catalog = CreateCatalog(
            documentation,
            typeof(AllDescriptionSourcesOptions),
            typeof(ConfigurationDescriptionOptions),
            typeof(XmlDescriptionOptions));

        var all = catalog.FindByFullName<ConfigurationBuildingBlock>(typeof(AllDescriptionSourcesOptions).FullName)!;
        all.MetadataDescription.Should().Be("Metadata wins.");
        all.ConfigurationDescription.Should().Be("Configuration fallback.");
        all.XmlDocumentationDescription.Should().Be("XML all.");
        all.Description.Should().Be("Metadata wins.");

        var configuration = catalog.FindByFullName<ConfigurationBuildingBlock>(typeof(ConfigurationDescriptionOptions).FullName)!;
        configuration.MetadataDescription.Should().BeNull();
        configuration.ConfigurationDescription.Should().Be("Configuration wins.");
        configuration.XmlDocumentationDescription.Should().Be("XML configuration.");
        configuration.Description.Should().Be("Configuration wins.");

        var xml = catalog.FindByFullName<ConfigurationBuildingBlock>(typeof(XmlDescriptionOptions).FullName)!;
        xml.MetadataDescription.Should().BeNull();
        xml.ConfigurationDescription.Should().BeNull();
        xml.XmlDocumentationDescription.Should().Be("XML only.");
        xml.Description.Should().Be("XML only.");
    }

    [Fact]
    public void CreateAnalyzed_ShouldEnrichConfigurationReloadBehaviorAfterConnectionsExist()
    {
        const string definitionKey = "Test.ReloadableOptions";
        var definition = CreateDefinition(definitionKey);
        var registry = Substitute.For<IConfigurationDefinitionRegistry>();
        registry.TryGet(definitionKey, out Arg.Any<ConfigurationDefinition?>())
            .Returns(callInfo =>
            {
                callInfo[1] = definition;
                return true;
            });
        var options = new ModuleBuildingBlocksOption();

        _ = BuildingBlockCatalog.CreateAnalyzed(
            options,
            options.Naming,
            NullLogger<BuildingBlockCatalog>.Instance,
            new[]
            {
                CreateShape(typeof(ReloadableOptions)),
                CreateShape(typeof(ReloadableOptionsConsumer))
            },
            registry);

        registry.Received(1).Register(Arg.Is<ConfigurationDefinition>(registered =>
            registered.DefinitionKey == definitionKey
            && registered.ReloadBehavior == ConfigurationReloadBehavior.OnlineReloadable
            && registered.ReloadBehaviorObservationKind == ConfigurationReloadBehaviorObservationKind.Inferred));
    }

    [Theory]
    [InlineData(null, EConfigurationUsageType.Unknown)]
    [InlineData(typeof(FrozenOptionsConsumer), EConfigurationUsageType.Offline)]
    [InlineData(typeof(FrozenSnapshotConsumer), EConfigurationUsageType.OnlineSnapshot)]
    [InlineData(typeof(FrozenMonitorConsumer), EConfigurationUsageType.OnlineMonitor)]
    public void StartupConfiguration_ShouldKeepRestartSemanticsRegardlessOfConsumerAccessor(
        Type? consumerType, EConfigurationUsageType usage)
    {
        const string definitionKey = "Test.FrozenOptions";
        var definition = CreateDefinition(definitionKey) with
        {
            Kind = ConfigurationKind.Startup,
            ReloadBehavior = ConfigurationReloadBehavior.RequiresRestart,
            ReloadBehaviorObservationKind = ConfigurationReloadBehaviorObservationKind.Declared
        };
        var registry = Substitute.For<IConfigurationDefinitionRegistry>();
        registry.TryGet(definitionKey, out Arg.Any<ConfigurationDefinition?>()).Returns(call =>
        {
            call[1] = definition;
            return true;
        });
        var types = consumerType is null ? new[] { typeof(OptionsFrozen) } : [typeof(OptionsFrozen), consumerType];
        var options = new ModuleBuildingBlocksOption();
        var catalog = BuildingBlockCatalog.CreateAnalyzed(options, options.Naming,
            NullLogger<BuildingBlockCatalog>.Instance, types.Select(CreateShape).ToArray(), registry);

        var unit = catalog.FindByFullName<ConfigurationBuildingBlock>(typeof(OptionsFrozen).FullName)!;
        if (consumerType is null) Assert.Empty(unit.ConfigurationDependencies);
        else
        {
            var dependency = Assert.Single(unit.ConfigurationDependencies);
            Assert.Equal(consumerType, dependency.Key.ClrType);
            Assert.Equal(usage, dependency.Value);
        }
        Assert.True(unit.IsOffline);
        Assert.Equal(ConfigurationReloadBehavior.RequiresRestart, unit.InferredReloadBehavior);
        Assert.DoesNotContain(unit.Alerts, alert => alert.Source == "ConfigurationReloadBehaviorInference");
        registry.DidNotReceive().Register(Arg.Any<ConfigurationDefinition>());
    }

    [Fact]
    public void AttachDocumentation_applies_once_and_keeps_analysis_results_observable()
    {
        var documentation = Substitute.For<IXmlDocumentationService>();
        documentation.GetTypeDocumentation(typeof(XmlDescriptionOptions)).Returns("First application.");
        var options = new ModuleBuildingBlocksOption();
        var catalog = BuildingBlockCatalog.CreateAnalyzed(
            options,
            options.Naming,
            NullLogger<BuildingBlockCatalog>.Instance,
            [CreateShape(typeof(XmlDescriptionOptions))],
            configurationDefinitionRegistry: null);

        catalog.AttachDocumentation(documentation);
        catalog.AttachDocumentation(null);

        catalog.FindByFullName<ConfigurationBuildingBlock>(typeof(XmlDescriptionOptions).FullName)!
            .XmlDocumentationDescription.Should().Be("First application.");
    }

    [Fact]
    public void DiscoveryPlan_ShouldPublishExactlyOnceAndRejectPartialReads()
    {
        var plan = new BuildingBlockDiscoveryPlan();

        Action readBeforePublish = () => _ = plan.GetRequiredSnapshot();
        readBeforePublish.Should().Throw<InvalidOperationException>()
            .WithMessage("*discovery has not completed*");

        plan.Publish([CreateShape(typeof(AnnotatedRequest))]);
        plan.GetRequiredSnapshot().Should().ContainSingle(shape =>
            shape.Type == typeof(AnnotatedRequest));

        Action publishAgain = () => plan.Publish([CreateShape(typeof(ResolverRequest))]);
        publishAgain.Should().Throw<InvalidOperationException>()
            .WithMessage("*published more than once*");
        plan.GetRequiredSnapshot().Should().ContainSingle(shape =>
            shape.Type == typeof(AnnotatedRequest));
    }

    [Fact]
    public void Empty_catalog_reports_no_data_instead_of_full_coverage()
    {
        var service = CreateService(CreateCatalog());

        var dashboard = service.GetDashboard();

        dashboard.TotalBuildingBlocks.Should().Be(0);
        dashboard.DistinctKinds.Should().Be(0);
        dashboard.Kinds.Should().BeEmpty();
        dashboard.Coverage.Should().HaveCount(4)
            .And.OnlyContain(metric => metric.Covered == 0 && metric.Total == 0 && metric.Percentage == null);
        dashboard.CoverageGaps.Should().BeEmpty();
    }

    [Fact]
    public async Task Detail_resolves_each_requirement_independently_and_rejects_unsafe_links()
    {
        var catalog = CreateCatalog(typeof(ResolverRequest));
        var resolver = new TestRequirementLinkResolver(requirementId => requirementId switch
        {
            "REQ-RESOLVED" => new BuildingBlockRequirementLink("Resolved requirement", "/requirements/REQ-RESOLVED"),
            "REQ-UNSAFE" => new BuildingBlockRequirementLink("Unsafe requirement", "javascript:alert(1)"),
            _ => throw new InvalidOperationException("Resolver unavailable.")
        });
        var service = CreateService(catalog, resolver);

        var detail = await service.GetDetailAsync(
            typeof(ResolverRequest).FullName!,
            TestContext.Current.CancellationToken);

        detail.Requirements.Should().HaveCount(3);
        detail.Requirements[0].Should().BeEquivalentTo(new BuildingBlockRequirementReference
        {
            Id = "REQ-RESOLVED",
            Title = "Resolved requirement",
            Href = "/requirements/REQ-RESOLVED"
        });
        detail.Requirements[1].IsResolved.Should().BeFalse();
        detail.Requirements[1].Title.Should().Be("Unsafe requirement");
        detail.Requirements[2].Should().Match<BuildingBlockRequirementReference>(reference =>
            reference.Id == "REQ-FAILED"
            && reference.Title == "REQ-FAILED"
            && !reference.IsResolved);
    }

    [Fact]
    public async Task Facade_returns_serializable_projections_without_runtime_reflection_members()
    {
        var catalog = CreateCatalog(typeof(CompleteDomainService), typeof(DependencyDomainService));
        var service = CreateService(catalog);
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(service)
            .BuildServiceProvider();
        var facade = new BuildingBlocksFacade(
            NullLogger<BuildingBlocksFacade>.Instance,
            serviceProvider);

        var listResult = await facade.GetAllAsync();
        var dashboardResult = await facade.GetDashboardAsync();
        var detailResult = await facade.GetDetailAsync(
            typeof(CompleteDomainService).FullName!,
            TestContext.Current.CancellationToken);

        listResult.Status.Should().Be(ResultStatus.Ok);
        dashboardResult.Status.Should().Be(ResultStatus.Ok);
        detailResult.Status.Should().Be(ResultStatus.Ok);
        detailResult.Data!.Summary.Dependencies.Should().ContainSingle(dependency =>
            dependency.Key == typeof(DependencyDomainService).FullName);

        var serialized = JsonSerializer.Serialize(detailResult.Data);
        serialized.Should().NotContain("MethodInfo");
        serialized.Should().NotContain("RuntimeType");
        serialized.Should().NotContain("ConstructorParameterTypes");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(detailResult.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var summary = json.RootElement.GetProperty("summary");
        summary.GetProperty("kind").GetString().Should().Be("DomainService");
        summary.TryGetProperty("unitType", out _).Should().BeFalse();
    }

    private static BuildingBlockCoverageMetric Metric(
        BuildingBlockDashboardSnapshot dashboard,
        BuildingBlockCoverageKind kind)
    {
        return dashboard.Coverage.Single(metric => metric.Kind == kind);
    }

    private static BuildingBlockCatalog CreateCatalog(params Type[] types)
    {
        return CreateCatalog(null, types);
    }

    private static BuildingBlockCatalog CreateCatalog(
        IXmlDocumentationService? documentation,
        params Type[] types)
    {
        var options = new ModuleBuildingBlocksOption();
        var catalog = BuildingBlockCatalog.CreateAnalyzed(
            options,
            options.Naming,
            NullLogger<BuildingBlockCatalog>.Instance,
            types.Select(CreateShape),
            configurationDefinitionRegistry: null);
        catalog.AttachDocumentation(documentation);
        return catalog;
    }

    private static BuildingBlockCatalog CreateNamingCatalog(Type type, BuildingBlockNamingMode mode,
        BuildingBlockNamingRule? configurationRule = null, bool enabled = true)
    {
        var options = new ModuleBuildingBlocksOption();
        options.Naming.Enabled = enabled;
        options.Naming.Mode = mode;
        if (configurationRule is not null)
            options.Naming.Rules[BuildingBlockKind.Configuration] = configurationRule;
        return BuildingBlockCatalog.CreateAnalyzed(options, options.Naming,
            NullLogger<BuildingBlockCatalog>.Instance, [CreateShape(type)], configurationDefinitionRegistry: null);
    }

    private static BusinessTypeShape CreateShape(Type type)
    {
        return (BusinessTypeShape)(Activator.CreateInstance(
            typeof(BusinessTypeShape),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [type],
            culture: null)
            ?? throw new InvalidOperationException("Could not create a business-type shape for the catalog test."));
    }

    private static BuildingBlockCatalogService CreateService(
        IBuildingBlockCatalog catalog,
        IBuildingBlockRequirementLinkResolver? resolver = null)
    {
        var serializerOptions = Substitute.For<IJsonSerializerOptionsProvider>();
        var eventBus = Substitute.For<IDistributedEventBus>();
        var application = new MonicaApplicationOptions
        {
            ProjectName = "Ordering.Api",
            AppId = "ordering-api",
            AppName = "Ordering Service",
            AppVersion = "2.3.0",
            DomainName = "Ordering"
        };

        var projections = new BuildingBlockProjectionService(
            catalog,
            application,
            resolver ?? new TestRequirementLinkResolver(_ => null),
            NullLogger<BuildingBlockProjectionService>.Instance);

        return new BuildingBlockCatalogService(
            eventBus,
            serializerOptions,
            catalog,
            projections);
    }

    private static ConfigurationDefinition CreateDefinition(string definitionKey)
    {
        return new ConfigurationDefinition
        {
            DefinitionKey = definitionKey,
            SectionPath = "Test:Reloadable",
            DisplayName = "Reloadable options",
            ClrTypeName = typeof(ReloadableOptions).AssemblyQualifiedName!,
            FromProject = "Test.Monica.BuildingBlocks",
            SchemaHash = "test",
            Root = new ConfigurationNodeDefinition
            {
                NodeKey = string.Empty,
                Name = nameof(ReloadableOptions),
                RelativePath = LogicalPath.Root,
                ConfigurationPath = "Test:Reloadable",
                ClrTypeName = typeof(ReloadableOptions).AssemblyQualifiedName!,
                NodeKind = ConfigurationNodeKind.Object
            }
        };
    }

    [BuildingBlockMetadata("  Approve Order  ",
        Owner = "  Ordering Team ",
        Tags = [" ordering ", "ORDERING", "approval"])]
    [BuildingBlockRequirement(" ord-req-001 ")]
    [BuildingBlockRequirement("ORD-REQ-001")]
    [BuildingBlockRequirement("ORD-REQ-002")]
    public sealed class AnnotatedRequest : IResultRequest
    {
    }

    [BuildingBlockMetadata("Inherited title")]
    public class MetadataBase
    {
    }

    public sealed class InheritedMetadataRequest : MetadataBase, IResultRequest
    {
    }

    [BuildingBlockMetadata(" ", Tags = ["", " valid "])]
    [BuildingBlockRequirement(" ")]
    public sealed class MalformedRequest : IResultRequest
    {
    }

    [BuildingBlockMetadata(
        "Complete service",
        Owner = "Ordering Team",
        Description = "Coordinates complete behavior.")]
    [BuildingBlockRequirement("ORD-REQ-100")]
    public sealed class CompleteDomainService(DependencyDomainService dependency) : DomainService
    {
        public DependencyDomainService Dependency { get; } = dependency;
    }

    public sealed class DependencyDomainService : DomainService
    {
    }

    [BuildingBlockRequirement("REQ-RESOLVED")]
    [BuildingBlockRequirement("REQ-UNSAFE")]
    [BuildingBlockRequirement("REQ-FAILED")]
    public sealed class ResolverRequest : IResultRequest
    {
    }

    public sealed class PriorityCrudApplicationService : ApplicationService, ICrudApplicationService;

    public sealed class CommonApplicationInfrastructure : ApplicationService;

    [BuildingBlockMetadata("Changed payload")]
    public sealed class EventChanged<T> : DomainEvent
    {
        public T? Value { get; set; }
    }

    public sealed class HierarchyNode
    {
        public HierarchyNode? Parent { get; set; }
    }

    public sealed class DistributedEventHandlerNodeChanged : IDistributedEventHandler<EventChanged<HierarchyNode>>
    {
        public Task HandleEventAsync(EventChanged<HierarchyNode> eventData, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class DistributedEventHandlerTextChanged : IDistributedEventHandler<EventChanged<string>>
    {
        public Task HandleEventAsync(EventChanged<string> eventData, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class LocalEventHandlerTextChanged : ILocalEventHandler<EventChanged<string>>
    {
        public Task HandleEventAsync(EventChanged<string> eventData, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class DistributedEventHandlerNumberChanged : IDistributedEventHandler<EventChanged<int>>
    {
        public Task HandleEventAsync(EventChanged<int> eventData, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class DistributedEventHandlerSingleChanged : DistributedEventHandler<EventChanged<string>>
    {
        public override Task HandleEventAsync(EventChanged<string> eventData, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    public sealed class DistributedEventHandlerBatchChanged : DistributedBatchEventHandler<EventChanged<string>>
    {
        public override Task HandleBatchAsync(IReadOnlyList<EventChanged<string>> events, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    public sealed class QueryDownload : IRequest<byte[]>, global::Monica.Core.Mediator.IQueryRequest;

    public sealed class QueryHandlerDownload : ApplicationRequestHandler<QueryDownload, byte[]>
    {
        public override Task<byte[]> Handle(QueryDownload request, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<byte>());
    }

    public abstract class ProjectRequestHandler<TRequest, TResponse> : ApplicationRequestHandler<TRequest, TResponse>
        where TRequest : IRequest<TResponse>;

    public sealed record QueryReadText : IQuery<string>;

    public sealed class QueryHandlerReadText : ProjectRequestHandler<QueryReadText, Result<string>>
    {
        public override Task<Result<string>> Handle(QueryReadText request, CancellationToken cancellationToken)
            => Task.FromResult(Result.Success("text"));
    }

    public sealed record CommandApply : ICommand;

    public sealed class CommandHandlerApply : ApplicationRequestHandler<CommandApply, Result>
    {
        public override Task<Result> Handle(CommandApply request, CancellationToken cancellationToken)
            => Task.FromResult(Result.Success());
    }

    public sealed record CustomResponse(string Value);

    public sealed record QueryReadEnvelope : IRequest<CustomResponse>;

    public sealed class QueryHandlerReadEnvelope : ApplicationRequestHandler<QueryReadEnvelope, CustomResponse>
    {
        public override Task<CustomResponse> Handle(QueryReadEnvelope request, CancellationToken cancellationToken)
            => Task.FromResult(new CustomResponse("custom"));
    }

    public sealed class PrioritySeederHostedService : ISeeder, IHostedService
    {
        public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Configuration(Description = "Configuration fallback.")]
    [BuildingBlockMetadata("All description sources", Description = "Metadata wins.")]
    public sealed class AllDescriptionSourcesOptions;

    [Configuration("Naming:Managed", DefinitionKey = "issued-managed-options")]
    public sealed class OptionsManagedNaming;

    [Configuration("Naming:Startup", Kind = ConfigurationKind.Startup, DefinitionKey = "issued-startup-options")]
    public sealed class OptionsStartupNaming;

    [Configuration("Naming:Legacy")]
    public sealed class LegacyNamingOptions;

    [Configuration(Description = "Configuration wins.")]
    public sealed class ConfigurationDescriptionOptions;

    [Configuration]
    public sealed class XmlDescriptionOptions;

    [Configuration(DefinitionKey = "Test.ReloadableOptions")]
    public sealed class ReloadableOptions;

    [Configuration("Test:Frozen", DefinitionKey = "Test.FrozenOptions", Kind = ConfigurationKind.Startup)]
    public sealed class OptionsFrozen;

    public sealed class FrozenOptionsConsumer(IOptions<OptionsFrozen> options) : DomainService
    {
        public IOptions<OptionsFrozen> Options { get; } = options;
    }

    public sealed class FrozenSnapshotConsumer(IOptionsSnapshot<OptionsFrozen> options) : DomainService
    {
        public IOptionsSnapshot<OptionsFrozen> Options { get; } = options;
    }

    public sealed class FrozenMonitorConsumer(IOptionsMonitor<OptionsFrozen> options) : DomainService
    {
        public IOptionsMonitor<OptionsFrozen> Options { get; } = options;
    }

    public sealed class ReloadableOptionsConsumer(IOptionsMonitor<ReloadableOptions> options) : DomainService
    {
        public IOptionsMonitor<ReloadableOptions> Options { get; } = options;
    }

    private sealed class TestRequirementLinkResolver(
        Func<string, BuildingBlockRequirementLink?> resolve)
        : IBuildingBlockRequirementLinkResolver
    {
        public ValueTask<BuildingBlockRequirementLink?> ResolveAsync(
            string requirementId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(resolve(requirementId));
        }
    }
}
