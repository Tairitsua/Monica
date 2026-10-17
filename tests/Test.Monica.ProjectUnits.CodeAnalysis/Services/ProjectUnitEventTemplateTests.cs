using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.DependencyInjection.Abstractions;
using Monica.EventBus.Annotations;
using Monica.EventBus.Events;
using Monica.Modules;
using Monica.ProjectUnits.Annotations;
using Monica.ProjectUnits.CodeAnalysis.Models;
using Monica.ProjectUnits.CodeAnalysis.Services;
using Monica.ProjectUnits.Models;
using Monica.ProjectUnits.Services.Support;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Xunit;

namespace Test.Monica.ProjectUnits.CodeAnalysis.Services;

public sealed class ProjectUnitEventTemplateTests
{
    [Fact]
    public void Compile_WhenCanonicalEventTemplatesAreUsed_ShouldDiscoverCompleteConnectedProjectUnits()
    {
        var sources = new[]
        {
            ReadExample("ProjectUnitTemplates.Events.md", "Domain Event"),
            ReadExample("ProjectUnitTemplates.Handlers.md", "Distributed Handler Example"),
            ReadExample("ProjectUnitTemplates.Handlers.md", "Local Handler Example"),
            COLLABORATOR_SOURCE
        };
        var cancellationToken = TestContext.Current.CancellationToken;
        var compilation = CreateCompilation(sources, cancellationToken);
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream, cancellationToken: cancellationToken);
        result.Diagnostics.Where(static diagnostic =>
                diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Should().BeEmpty();

        var assembly = Assembly.Load(stream.ToArray());
        var options = new ModuleProjectUnitsOption();
        options.ConventionOptions.EnableNameConvention = true;
        var catalog = ProjectUnitCatalog.CreateAnalyzed(
            options,
            options.ConventionOptions,
            NullLogger<ProjectUnitCatalog>.Instance,
            assembly.GetTypes().Select(CreateShape),
            configurationDefinitionRegistry: null);

        var eventUnit = catalog.GetUnits<UnitDomainEvent>().Should().ContainSingle().Which;
        var distributedUnit = catalog.GetUnits<UnitDomainEventHandler>().Should().ContainSingle().Which;
        var localUnit = catalog.GetUnits<UnitLocalEventHandler>().Should().ContainSingle().Which;
        eventUnit.Type.IsSubclassOf(typeof(DomainEvent)).Should().BeTrue();
        eventUnit.Type.Name.Should().Be("EventOrderApproved");
        eventUnit.Type.Namespace.Should().Be("Platform.Protocol.PublishedLanguages.DomainOrdering.Events");
        EventNameAttribute.GetNameOrDefault(eventUnit.Type).Should().Be("ordering.order-approved");
        distributedUnit.Type.BaseType!.GetGenericTypeDefinition().Should().Be(typeof(DomainEventHandler<>));
        localUnit.Type.BaseType!.GetGenericTypeDefinition().Should().Be(typeof(LocalEventHandler<>));
        distributedUnit.EventType.Should().Be(eventUnit.Type);
        localUnit.EventType.Should().Be(eventUnit.Type);
        distributedUnit.DependencyUnits.Should().Contain(eventUnit);
        localUnit.DependencyUnits.Should().Contain(eventUnit);
        catalog.GetAllUnits().Should().HaveCount(3);

        var classifier = new ProjectUnitSymbolClassifier();
        foreach (var runtimeUnit in catalog.GetAllUnits())
        {
            var symbol = compilation.GetTypeByMetadataName(runtimeUnit.Key)!;
            var sourceUnit = classifier.Classify(symbol);
            sourceUnit.Should().NotBeNull();
            sourceUnit!.UnitType.Should().Be(
                Enum.Parse<ProjectUnitSourceType>(runtimeUnit.UnitType.ToString()));
            sourceUnit.ExecutionPoints.Should().Equal(runtimeUnit.ExecutionPoints);
            sourceUnit.HasExplicitMetadata.Should().BeTrue();
            sourceUnit.Owner.Should().BeNull();
            sourceUnit.RequirementIds.Should().Equal("ORD-REQ-001");
            sourceUnit.Diagnostics.Should().BeEmpty();
            if (runtimeUnit is UnitDomainEventHandler or UnitLocalEventHandler)
            {
                sourceUnit.Dependencies.Should().Contain(dependency =>
                    dependency.Symbol.GetRuntimeName() == eventUnit.Key);
            }

            runtimeUnit.HasExplicitMetadata.Should().BeTrue();
            runtimeUnit.Owner.Should().Be(sourceUnit.Owner);
            runtimeUnit.RequirementIds.Should().Equal(sourceUnit.RequirementIds);
            runtimeUnit.MetadataTitle.Should().Be(sourceUnit.Title);
            runtimeUnit.MetadataDescription.Should().Be(sourceUnit.Description).And.NotBeNullOrWhiteSpace();
            runtimeUnit.Tags.Should().Equal(sourceUnit.Tags).And.Equal("ordering", "approval");
            runtimeUnit.Alerts.Should().BeEmpty();
        }
    }

    private static string ReadExample(string resourceName, string sectionName)
    {
        using var resource = typeof(ProjectUnitEventTemplateTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Template resource '{resourceName}' is missing.");
        using var reader = new StreamReader(resource);
        var section = Regex.Match(
            reader.ReadToEnd(),
            $@"(?ms)^## {Regex.Escape(sectionName)}\r?\n(?<body>.*?)(?=^## |\z)");
        section.Success.Should().BeTrue($"the template must contain its {sectionName} example");
        var block = Regex.Match(section.Groups["body"].Value, @"(?ms)^```csharp\r?\n(?<code>.*?)^```");
        block.Success.Should().BeTrue($"{sectionName} must provide executable C# guidance");
        var source = block.Groups["code"].Value;
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["$ContractNamespace$"] = "Platform.Protocol.PublishedLanguages.DomainOrdering",
            ["$ApplicationNamespace$"] = "OrderingService.API",
            ["$DomainNamespace$"] = "OrderingService.Domain",
            ["$SubdomainTag$"] = "ordering",
            ["$FeatureTag$"] = "approval",
            ["$RequirementId$"] = "ORD-REQ-001"
        };
        foreach (var (placeholder, value) in placeholders)
        {
            source = source.Replace(placeholder, value, StringComparison.Ordinal);
        }

        Regex.IsMatch(source, @"\$[A-Za-z][A-Za-z0-9]*\$").Should().BeFalse("every template input must be supplied");
        return source;
    }

    private static CSharpCompilation CreateCompilation(IEnumerable<string> sources, CancellationToken cancellationToken)
    {
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var projectAssemblies = new[]
        {
            typeof(DomainEvent).Assembly.Location,
            typeof(DomainEventHandler<>).Assembly.Location,
            typeof(ProjectUnitMetadataAttribute).Assembly.Location,
            typeof(ICachedServiceProvider).Assembly.Location
        };
        var references = trustedAssemblies.Concat(projectAssemblies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
            "CanonicalProjectUnitEventTemplates",
            sources.Select(source => CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(LanguageVersion.Preview),
                cancellationToken: cancellationToken)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static BusinessTypeShape CreateShape(Type type)
        => (BusinessTypeShape)Activator.CreateInstance(
            typeof(BusinessTypeShape),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [type],
            culture: null)!;

    private const string COLLABORATOR_SOURCE = """
        global using System;
        global using System.Threading;
        global using System.Threading.Tasks;

        namespace OrderingService.Domain.DomainServices;

        public sealed class DomainNotifyWarehouse
        {
            public Task ExecuteAsync(long orderId, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        public sealed class DomainRefreshReadModel
        {
            public Task ExecuteAsync(long orderId, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }
        """;
}
