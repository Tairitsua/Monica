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
using Monica.Core.TypeDiscovery.Models;
using Monica.Core.XmlDocumentation.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.Framework.Seeder.Abstractions;
using Monica.Modules;
using Monica.ProjectUnits.Abstractions;
using Monica.ProjectUnits.Annotations;
using Monica.ProjectUnits.Facades;
using Monica.ProjectUnits.Models;
using Monica.ProjectUnits.Services;
using Monica.ProjectUnits.Services.Support;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Monica.WebApi.AutoControllers.Abstractions;
using NSubstitute;
using Xunit;

namespace Test.Monica.ProjectUnits;

public sealed class ProjectUnitCatalogTests
{
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
            "ProjectUnit.Metadata.Title.Empty",
            "ProjectUnit.Metadata.Tag.Empty",
            "ProjectUnit.Requirement.Id.Empty");
    }

    [Fact]
    public void Discovery_preserves_specific_category_priority_for_multi_role_types()
    {
        var catalog = CreateCatalog(
            typeof(PriorityCrudApplicationService),
            typeof(PrioritySeederHostedService));

        catalog.GetAllUnits().Should().HaveCount(2);
        catalog.FindByFullName(typeof(PriorityCrudApplicationService).FullName)
            .Should().BeOfType<UnitCrudApplicationService>();
        catalog.FindByFullName(typeof(PrioritySeederHostedService).FullName)
            .Should().BeOfType<UnitSeeder>();
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

        dashboard.TotalUnits.Should().Be(2);
        dashboard.DistinctUnitTypes.Should().Be(1);
        Metric(dashboard, ProjectUnitCoverageKind.Metadata).Percentage.Should().Be(50m);
        Metric(dashboard, ProjectUnitCoverageKind.Description).Percentage.Should().Be(100m);
        Metric(dashboard, ProjectUnitCoverageKind.Ownership).Percentage.Should().Be(50m);
        Metric(dashboard, ProjectUnitCoverageKind.Requirements).Percentage.Should().Be(50m);
        dashboard.Dependencies.EdgeCount.Should().Be(1);
        dashboard.Dependencies.IsolatedUnitCount.Should().Be(0);
        dashboard.CoverageGaps.Should().ContainSingle(gap =>
            gap.Unit.Key == typeof(DependencyDomainService).FullName
            && gap.MissingCoverage.Contains(ProjectUnitCoverageKind.Metadata)
            && gap.MissingCoverage.Contains(ProjectUnitCoverageKind.Ownership)
            && gap.MissingCoverage.Contains(ProjectUnitCoverageKind.Requirements));
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

        var all = catalog.FindByFullName<UnitConfiguration>(typeof(AllDescriptionSourcesOptions).FullName)!;
        all.MetadataDescription.Should().Be("Metadata wins.");
        all.ConfigurationDescription.Should().Be("Configuration fallback.");
        all.XmlDocumentationDescription.Should().Be("XML all.");
        all.Description.Should().Be("Metadata wins.");

        var configuration = catalog.FindByFullName<UnitConfiguration>(typeof(ConfigurationDescriptionOptions).FullName)!;
        configuration.MetadataDescription.Should().BeNull();
        configuration.ConfigurationDescription.Should().Be("Configuration wins.");
        configuration.XmlDocumentationDescription.Should().Be("XML configuration.");
        configuration.Description.Should().Be("Configuration wins.");

        var xml = catalog.FindByFullName<UnitConfiguration>(typeof(XmlDescriptionOptions).FullName)!;
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
        var options = new ModuleProjectUnitsOption();

        _ = ProjectUnitCatalog.CreateAnalyzed(
            options,
            options.ConventionOptions,
            NullLogger<ProjectUnitCatalog>.Instance,
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

    [Fact]
    public void AttachDocumentation_applies_once_and_keeps_analysis_results_observable()
    {
        var documentation = Substitute.For<IXmlDocumentationService>();
        documentation.GetTypeDocumentation(typeof(XmlDescriptionOptions)).Returns("First application.");
        var options = new ModuleProjectUnitsOption();
        var catalog = ProjectUnitCatalog.CreateAnalyzed(
            options,
            options.ConventionOptions,
            NullLogger<ProjectUnitCatalog>.Instance,
            [CreateShape(typeof(XmlDescriptionOptions))],
            configurationDefinitionRegistry: null);

        catalog.AttachDocumentation(documentation);
        catalog.AttachDocumentation(null);

        catalog.FindByFullName<UnitConfiguration>(typeof(XmlDescriptionOptions).FullName)!
            .XmlDocumentationDescription.Should().Be("First application.");
    }

    [Fact]
    public void DiscoveryPlan_ShouldPublishExactlyOnceAndRejectPartialReads()
    {
        var plan = new ProjectUnitDiscoveryPlan();

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

        dashboard.TotalUnits.Should().Be(0);
        dashboard.DistinctUnitTypes.Should().Be(0);
        dashboard.UnitTypes.Should().BeEmpty();
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
            "REQ-RESOLVED" => new ProjectUnitRequirementLink("Resolved requirement", "/requirements/REQ-RESOLVED"),
            "REQ-UNSAFE" => new ProjectUnitRequirementLink("Unsafe requirement", "javascript:alert(1)"),
            _ => throw new InvalidOperationException("Resolver unavailable.")
        });
        var service = CreateService(catalog, resolver);

        var detail = await service.GetProjectUnitDetailAsync(
            typeof(ResolverRequest).FullName!,
            TestContext.Current.CancellationToken);

        detail.Requirements.Should().HaveCount(3);
        detail.Requirements[0].Should().BeEquivalentTo(new ProjectUnitRequirementReference
        {
            Id = "REQ-RESOLVED",
            Title = "Resolved requirement",
            Href = "/requirements/REQ-RESOLVED"
        });
        detail.Requirements[1].IsResolved.Should().BeFalse();
        detail.Requirements[1].Title.Should().Be("Unsafe requirement");
        detail.Requirements[2].Should().Match<ProjectUnitRequirementReference>(reference =>
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
        var facade = new ProjectUnitsFacade(
            NullLogger<ProjectUnitsFacade>.Instance,
            serviceProvider);

        var listResult = await facade.GetAllProjectUnitsAsync();
        var dashboardResult = await facade.GetDashboardAsync();
        var detailResult = await facade.GetProjectUnitDetailAsync(
            typeof(CompleteDomainService).FullName!,
            TestContext.Current.CancellationToken);

        listResult.Status.Should().Be(ResStatus.Ok);
        dashboardResult.Status.Should().Be(ResStatus.Ok);
        detailResult.Status.Should().Be(ResStatus.Ok);
        detailResult.Data!.Summary.Dependencies.Should().ContainSingle(dependency =>
            dependency.Key == typeof(DependencyDomainService).FullName);

        var serialized = JsonSerializer.Serialize(detailResult.Data);
        serialized.Should().NotContain("MethodInfo");
        serialized.Should().NotContain("RuntimeType");
        serialized.Should().NotContain("ConstructorParameterTypes");
    }

    private static ProjectUnitCoverageMetric Metric(
        ProjectUnitDashboardSnapshot dashboard,
        ProjectUnitCoverageKind kind)
    {
        return dashboard.Coverage.Single(metric => metric.Kind == kind);
    }

    private static ProjectUnitCatalog CreateCatalog(params Type[] types)
    {
        return CreateCatalog(null, types);
    }

    private static ProjectUnitCatalog CreateCatalog(
        IXmlDocumentationService? documentation,
        params Type[] types)
    {
        var options = new ModuleProjectUnitsOption();
        var catalog = ProjectUnitCatalog.CreateAnalyzed(
            options,
            options.ConventionOptions,
            NullLogger<ProjectUnitCatalog>.Instance,
            types.Select(CreateShape),
            configurationDefinitionRegistry: null);
        catalog.AttachDocumentation(documentation);
        return catalog;
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

    private static ProjectUnitCatalogService CreateService(
        IProjectUnitCatalog catalog,
        IProjectUnitRequirementLinkResolver? resolver = null)
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

        var projections = new ProjectUnitProjectionService(
            catalog,
            application,
            resolver ?? new TestRequirementLinkResolver(_ => null),
            NullLogger<ProjectUnitProjectionService>.Instance);

        return new ProjectUnitCatalogService(
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
            FromProject = "Test.Monica.ProjectUnits",
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

    [ProjectUnitMetadata("  Approve Order  ",
        Owner = "  Ordering Team ",
        Tags = [" ordering ", "ORDERING", "approval"])]
    [ProjectUnitRequirement(" ord-req-001 ")]
    [ProjectUnitRequirement("ORD-REQ-001")]
    [ProjectUnitRequirement("ORD-REQ-002")]
    public sealed class AnnotatedRequest : IResultRequest
    {
    }

    [ProjectUnitMetadata("Inherited title")]
    public class MetadataBase
    {
    }

    public sealed class InheritedMetadataRequest : MetadataBase, IResultRequest
    {
    }

    [ProjectUnitMetadata(" ", Tags = ["", " valid "])]
    [ProjectUnitRequirement(" ")]
    public sealed class MalformedRequest : IResultRequest
    {
    }

    [ProjectUnitMetadata(
        "Complete service",
        Owner = "Ordering Team",
        Description = "Coordinates complete behavior.")]
    [ProjectUnitRequirement("ORD-REQ-100")]
    public sealed class CompleteDomainService(DependencyDomainService dependency) : DomainService
    {
        public DependencyDomainService Dependency { get; } = dependency;
    }

    public sealed class DependencyDomainService : DomainService
    {
    }

    [ProjectUnitRequirement("REQ-RESOLVED")]
    [ProjectUnitRequirement("REQ-UNSAFE")]
    [ProjectUnitRequirement("REQ-FAILED")]
    public sealed class ResolverRequest : IResultRequest
    {
    }

    public sealed class PriorityCrudApplicationService : ApplicationService, ICrudApplicationService;

    public sealed class PrioritySeederHostedService : ISeeder, IHostedService
    {
        public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Configuration(Description = "Configuration fallback.")]
    [ProjectUnitMetadata("All description sources", Description = "Metadata wins.")]
    public sealed class AllDescriptionSourcesOptions;

    [Configuration(Description = "Configuration wins.")]
    public sealed class ConfigurationDescriptionOptions;

    [Configuration]
    public sealed class XmlDescriptionOptions;

    [Configuration(DefinitionKey = "Test.ReloadableOptions")]
    public sealed class ReloadableOptions;

    public sealed class ReloadableOptionsConsumer(IOptionsMonitor<ReloadableOptions> options) : DomainService
    {
        public IOptionsMonitor<ReloadableOptions> Options { get; } = options;
    }

    private sealed class TestRequirementLinkResolver(
        Func<string, ProjectUnitRequirementLink?> resolve)
        : IProjectUnitRequirementLinkResolver
    {
        public ValueTask<ProjectUnitRequirementLink?> ResolveAsync(
            string requirementId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(resolve(requirementId));
        }
    }
}
