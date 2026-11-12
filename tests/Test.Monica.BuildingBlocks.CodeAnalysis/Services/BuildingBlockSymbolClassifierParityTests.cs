using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Configuration.Annotations;
using Monica.Core.Execution.Mvc;
using Monica.Core.HostedService;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Annotations;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.DependencyInjection.Abstractions;
using Monica.EventBus;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Events;
using Monica.Framework.Seeder;
using Monica.Framework.Seeder.Abstractions;
using Monica.JobScheduler;
using Monica.JobScheduler.Abstractions;
using Monica.Modules;
using Monica.BuildingBlocks.Annotations;
using Monica.BuildingBlocks.CodeAnalysis.Models;
using Monica.BuildingBlocks.CodeAnalysis.Services;
using Monica.BuildingBlocks.Models;
using Monica.BuildingBlocks.Services.Support;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Services;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Monica.WebApi.AutoControllers.Abstractions;
using Xunit;

namespace Test.Monica.BuildingBlocks.CodeAnalysis.Services;

public sealed class BuildingBlockSymbolClassifierParityTests
{
    [Fact]
    public void Classify_WhenGivenTheSameCompiledTypes_ShouldMatchRuntimeCatalog()
    {
        var compilation = CreateCompilation(CLASSIFICATION_SOURCE);
        var classifier = new BuildingBlockSymbolClassifier();
        var sourceUnits = GetDeclaredTypes(compilation.Assembly.GlobalNamespace)
            .Select(classifier.Classify)
            .Where(static candidate => candidate is not null)
            .Select(static candidate => candidate!)
            .ToDictionary(
                static candidate => candidate.Symbol.GetRuntimeName(),
                static candidate => new Classification(candidate.Kind, candidate.ExecutionPoints),
                StringComparer.Ordinal);
        var options = new ModuleBuildingBlocksOption();
        var runtimeCatalog = new BuildingBlockCatalog(
            options,
            options.Naming,
            NullLogger<BuildingBlockCatalog>.Instance);
        var assembly = EmitAndLoad(compilation);

        runtimeCatalog.Discover(assembly.GetTypes()
            .Where(static type => !type.IsDefined(
                typeof(ExcludeFromBusinessTypeDiscoveryAttribute),
                inherit: false))
            .Select(CreateShape));
        var runtimeUnits = runtimeCatalog.GetAll().ToDictionary(
            static unit => unit.Key,
            static unit => new Classification(ToSourceType(unit.Kind), unit.ExecutionPoints),
            StringComparer.Ordinal);

        sourceUnits.Should().BeEquivalentTo(runtimeUnits);
        sourceUnits["Samples.SampleDirectController"].Should().BeEquivalentTo(new Classification(
            SourceBuildingBlockKind.HttpApi,
            [MvcExecutionPoints.Action.Value]));
        sourceUnits["Samples.SampleMediatedController"].ExecutionPoints.Should().BeEmpty();
        sourceUnits["Samples.SampleCrudService"].Kind.Should().Be(SourceBuildingBlockKind.CrudApplicationService);
        sourceUnits["Samples.CommandCustom"].Should().BeEquivalentTo(new Classification(
            SourceBuildingBlockKind.ApplicationRequestHandler, [MediatorExecutionPoints.Request.Value]));
        sourceUnits.Should().NotContainKey("Samples.SampleApplicationInfrastructure");
        sourceUnits.Should().NotContainKey("Samples.SampleNativeHandler");
        sourceUnits["Samples.SampleSeeder"].Should().BeEquivalentTo(new Classification(
            SourceBuildingBlockKind.Seeder,
            [SeederExecutionPoints.Run.Value]));
        sourceUnits["Samples.SamplePlainHostedService"].ExecutionPoints.Should().BeEmpty();
        sourceUnits["Samples.SampleBatchHandler"].Should().BeEquivalentTo(new Classification(
            SourceBuildingBlockKind.DistributedEventHandler, [EventBusExecutionPoints.DistributedBatchHandler.Value]));
        sourceUnits["Samples.SampleMonicaHostedService"].Should().BeEquivalentTo(new Classification(
            SourceBuildingBlockKind.HostedService,
            [
                HostedServiceExecutionPoints.Start.Value,
                HostedServiceExecutionPoints.Stop.Value,
                HostedServiceExecutionPoints.WorkItem.Value
            ]));
        sourceUnits["Samples.SampleMonicaBackgroundService"].Should().BeEquivalentTo(new Classification(
            SourceBuildingBlockKind.HostedService,
            [
                HostedServiceExecutionPoints.Start.Value,
                HostedServiceExecutionPoints.Stop.Value,
                HostedServiceExecutionPoints.WorkItem.Value
            ]));
        sourceUnits.Should().NotContainKey("Samples.SampleExcludedDomainService");
        sourceUnits["Samples.SampleIncludedDerivedDomainService"].Kind.Should()
            .Be(SourceBuildingBlockKind.DomainService);
        sourceUnits["Samples.SampleConfigurationBase"].Kind.Should()
            .Be(SourceBuildingBlockKind.Configuration);
        sourceUnits["Samples.SampleNativeRequest"].Kind.Should()
            .Be(SourceBuildingBlockKind.RequestDto);
        sourceUnits.Should().NotContainKey("Samples.SampleInheritedConfiguration");
        sourceUnits.Values.Select(static unit => unit.Kind).Distinct().Should()
            .BeEquivalentTo(BuildingBlockSourceAnalysisContract.DiscoverableKinds);
    }

    [Fact]
    public void Classify_WhenDiscoveryControlAttributesDifferByInheritance_ShouldMatchRuntimeRules()
    {
        var compilation = CreateCompilation(CLASSIFICATION_SOURCE);
        var classifier = new BuildingBlockSymbolClassifier();

        classifier.Classify(GetType(compilation, "Samples.SampleExcludedDomainService")).Should().BeNull();
        classifier.Classify(GetType(compilation, "Samples.SampleIncludedDerivedDomainService"))!.Kind.Should()
            .Be(SourceBuildingBlockKind.DomainService);
        classifier.Classify(GetType(compilation, "Samples.SampleConfigurationBase"))!.Kind.Should()
            .Be(SourceBuildingBlockKind.Configuration);
        classifier.Classify(GetType(compilation, "Samples.SampleInheritedConfiguration")).Should().BeNull();
    }

    [Fact]
    public void SourceTypeContract_ShouldMatchRuntimeBuildingBlockTypeNamesAndValues()
    {
        Enum.GetValues<SourceBuildingBlockKind>()
            .Select(static type => new { Name = type.ToString(), Value = (int)type })
            .Should().Equal(Enum.GetValues<BuildingBlockKind>()
                .Select(static type => new { Name = type.ToString(), Value = (int)type }));
    }

    [Fact]
    public void Classify_WhenMetadataAndRequirementsRepeat_ShouldNormalizeArchitectureContext()
    {
        var compilation = CreateCompilation(CLASSIFICATION_SOURCE);
        var classifier = new BuildingBlockSymbolClassifier();

        var candidate = classifier.Classify(GetType(compilation, "Samples.CommandCustom"));

        candidate.Should().NotBeNull();
        candidate!.Title.Should().Be("Custom command");
        candidate.Description.Should().Be("Coordinates a custom command.");
        candidate.Owner.Should().Be("Flight Team");
        candidate.Tags.Should().Equal("flight", "command");
        candidate.RequirementIds.Should().Equal("FIPS-FLIGHT-001", "FIPS-FLIGHT-002");
        candidate.HasExplicitMetadata.Should().BeTrue();
        candidate.ExecutionPoints.Should().Equal(MediatorExecutionPoints.Request.Value);
        candidate.Dependencies.Select(dependency => dependency.Symbol.Name).Should().Contain(
            "SampleTypedRequest",
            "SampleDomainService");
        classifier.Classify(GetType(compilation, "Samples.AbstractDomainService")).Should().BeNull();
        classifier.Classify(GetType(compilation, "Samples.GenericDomainService`1")).Should().BeNull();
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var projectAssemblies = new[]
        {
            typeof(ControllerBase).Assembly.Location,
            typeof(DbContext).Assembly.Location,
            typeof(IServiceScopeFactory).Assembly.Location,
            typeof(IHostedService).Assembly.Location,
            typeof(ILogger).Assembly.Location,
            typeof(NullLogger).Assembly.Location,
            typeof(IOptions<>).Assembly.Location,
            typeof(ApplicationService).Assembly.Location,
            typeof(BuildingBlockMetadataAttribute).Assembly.Location,
            typeof(ConfigurationAttribute).Assembly.Location,
            typeof(MoHostedService).Assembly.Location,
            typeof(ExcludeFromBusinessTypeDiscoveryAttribute).Assembly.Location,
            typeof(IObservableInstanceRegistry).Assembly.Location,
            typeof(ICachedServiceProvider).Assembly.Location,
            typeof(IDomainEvent).Assembly.Location,
            typeof(ISeeder).Assembly.Location,
            typeof(RecurringJob).Assembly.Location,
            typeof(IEntity).Assembly.Location,
            typeof(IRepository<>).Assembly.Location,
            typeof(ICrudApplicationService).Assembly.Location,
            typeof(IDistributedEventHandler<>).Assembly.Location,
            typeof(MediatedControllerAttribute).Assembly.Location
        };
        var references = trustedAssemblies.Concat(projectAssemblies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
            "BuildingBlockClassificationParitySamples",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static Assembly EmitAndLoad(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        emit.Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
        stream.Position = 0;
        return Assembly.Load(stream.ToArray());
    }

    private static BusinessTypeShape CreateShape(Type type)
    {
        return (BusinessTypeShape)(Activator.CreateInstance(
            typeof(BusinessTypeShape),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [type],
            culture: null)
            ?? throw new InvalidOperationException($"Could not create a business-type shape for {type.FullName}."));
    }

    private static IEnumerable<INamedTypeSymbol> GetDeclaredTypes(INamespaceSymbol namespaceSymbol)
    {
        return namespaceSymbol.GetTypeMembers()
            .Concat(namespaceSymbol.GetNamespaceMembers().SelectMany(GetDeclaredTypes));
    }

    private static INamedTypeSymbol GetType(Compilation compilation, string metadataName)
        => compilation.GetTypeByMetadataName(metadataName)
           ?? throw new InvalidOperationException($"Test type '{metadataName}' was not compiled.");

    private sealed record Classification(
        SourceBuildingBlockKind Kind,
        IReadOnlyList<string> ExecutionPoints);

    private static SourceBuildingBlockKind ToSourceType(BuildingBlockKind type)
        => Enum.Parse<SourceBuildingBlockKind>(type.ToString());

    private const string CLASSIFICATION_SOURCE = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        using Microsoft.Extensions.Logging;
        using Microsoft.Extensions.Logging.Abstractions;
        using Microsoft.Extensions.Options;
        using Monica.Configuration.Annotations;
        using Monica.Core.Execution.Mvc;
        using Monica.Core.Mediator;
        using Monica.Core.HostedService.Abstractions;
        using Monica.Core.Modularity.Annotations;
        using Monica.Core.ObservableInstance.Abstractions;
        using Monica.DependencyInjection.Abstractions;
        using Monica.EventBus.Abstractions.Handlers;
        using Monica.EventBus.Events;
        using Monica.Framework.Seeder.Abstractions;
        using Monica.JobScheduler.Abstractions;
        using Monica.Modules;
        using Monica.BuildingBlocks.Annotations;
        using Monica.Repository.Entity.Abstractions;
        using Monica.Repository.Persistence.Abstractions;
        using Monica.Repository.Persistence.Services;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Monica.WebApi.AutoControllers.Abstractions;

        namespace Samples;

        public sealed class SampleRequest : IResultRequest { }
        public sealed class SampleTypedRequest : IResultRequest<string> { }
        public sealed class SampleNativeRequest : IRequest<byte[]> { }
        public sealed class QueryHandlerDownload : ApplicationRequestHandler<SampleNativeRequest, byte[]>
        {
            public override Task<byte[]> Handle(SampleNativeRequest request, CancellationToken cancellationToken)
                => Task.FromResult(Array.Empty<byte>());
        }

        public abstract class OurApplicationRequestHandler<TRequest, TResponse> : ApplicationRequestHandler<TRequest, TResponse>
            where TRequest : IRequest<TResponse> { }

        [BuildingBlockMetadata("  Custom command  ", Owner = " Flight Team ",
            Description = " Coordinates a custom command. ", Tags = [" flight ", "FLIGHT", "command"])]
        [BuildingBlockRequirement(" FIPS-FLIGHT-001 ")]
        [BuildingBlockRequirement("fips-flight-001")]
        [BuildingBlockRequirement("FIPS-FLIGHT-002")]
        public sealed class CommandCustom(SampleDomainService service)
            : OurApplicationRequestHandler<SampleTypedRequest, Monica.Core.Results.Result<string>>
        {
            public override Task<Monica.Core.Results.Result<string>> Handle(
                SampleTypedRequest request,
                CancellationToken cancellationToken) => throw new NotImplementedException();
        }

        public sealed class SampleCrudService : ApplicationService, ICrudApplicationService { }
        public sealed class SampleApplicationInfrastructure : ApplicationService { }
        public sealed class SampleNativeHandler : IRequestHandler<SampleNativeRequest, byte[]>
        {
            public Task<byte[]> Handle(SampleNativeRequest request, CancellationToken cancellationToken)
                => Task.FromResult(Array.Empty<byte>());
        }
        public sealed class SampleDirectController : ControllerBase { }
        [MediatedController] public sealed class SampleMediatedController : ControllerBase { }
        [Configuration(DisplayName = "Options")] public sealed class SampleOptions { }
        [Configuration(DisplayName = "Base options")] public class SampleConfigurationBase { }
        public sealed class SampleInheritedConfiguration : SampleConfigurationBase { }
        public sealed class SampleEvent : DomainEvent { }
        public sealed class SampleDistributedHandler : IDistributedEventHandler<SampleEvent>
        {
            public Task HandleEventAsync(SampleEvent eventData, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }
        public sealed class SampleLocalHandler : ILocalEventHandler<SampleEvent>
        {
            public Task HandleEventAsync(SampleEvent eventData, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }
        public sealed class SampleBatchHandler : DistributedBatchEventHandler<SampleEvent>
        {
            public override Task HandleBatchAsync(System.Collections.Generic.IReadOnlyList<SampleEvent> events,
                CancellationToken cancellationToken) => Task.CompletedTask;
        }
        public sealed class SampleEntity : Entity<int> { }
        public sealed class SampleDbContext(
            DbContextOptions<SampleDbContext> options,
            ICachedServiceProvider services)
            : RepositoryDbContext<SampleDbContext>(options, services) { }
        public interface IRepositorySampleEntity : IRepository<SampleEntity> { }
        public sealed class RepositorySampleEntity(SampleDbContext context)
            : EfRepository<SampleDbContext, SampleEntity>(context), IRepositorySampleEntity { }
        public sealed class SampleDomainService : DomainService { }
        [ExcludeFromBusinessTypeDiscovery]
        public sealed class SampleExcludedDomainService : DomainService { }
        [ExcludeFromBusinessTypeDiscovery]
        public abstract class SampleExcludedDomainServiceBase : DomainService { }
        public sealed class SampleIncludedDerivedDomainService : SampleExcludedDomainServiceBase { }
        public abstract class AbstractDomainService : DomainService { }
        public sealed class GenericDomainService<T> : DomainService { }
        public sealed class SampleRecurringJob : RecurringJob
        {
            public SampleRecurringJob() : base(NullLogger.Instance) { }
            public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
        public sealed class SampleTriggeredJob : TriggeredJob<SampleRequest>
        {
            public SampleTriggeredJob() : base(NullLogger.Instance) { }
            public override Task ExecuteAsync(SampleRequest parameters, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }
        public sealed class SampleSeeder : ISeeder
        {
            public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
        public sealed class SamplePlainHostedService : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
        public sealed class SampleMonicaHostedService(
            IObservableInstanceRegistry observableInstances,
            IOptions<ModuleHostedServiceOption> options,
            IServiceScopeFactory scopeFactory,
            ILogger<SampleMonicaHostedService> logger)
            : MoHostedService(observableInstances, options, scopeFactory, logger)
        {
        }
        public sealed class SampleMonicaBackgroundService(
            IObservableInstanceRegistry observableInstances,
            IOptions<ModuleHostedServiceOption> options,
            IServiceScopeFactory scopeFactory,
            ILogger<SampleMonicaBackgroundService> logger)
            : MoBackgroundService(observableInstances, options, scopeFactory, logger)
        {
            protected override Task ExecuteBackgroundAsync(CancellationToken stoppingToken) => Task.CompletedTask;
        }
        """;
}
