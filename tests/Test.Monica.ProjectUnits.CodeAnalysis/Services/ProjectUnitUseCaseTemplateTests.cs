using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.DependencyInjection.Abstractions;
using Monica.Modules;
using Monica.ProjectUnits.Annotations;
using Monica.ProjectUnits.CodeAnalysis.Models;
using Monica.ProjectUnits.CodeAnalysis.Services;
using Monica.ProjectUnits.Models;
using Monica.ProjectUnits.Services.Support;
using Monica.Repository.Persistence.Abstractions;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Xunit;

namespace Test.Monica.ProjectUnits.CodeAnalysis.Services;

public sealed class ProjectUnitUseCaseTemplateTests
{
    [Fact]
    public async Task Compile_WhenConnectedCanonicalUseCaseIsUsed_ShouldExecuteInvariantsAndMatchDiscovery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sources = ReadExamples().Append(REPOSITORY_PROBE_SOURCE).Append(CUSTOM_HANDLER_SOURCE);
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
            assembly.GetTypes()
                .Where(static type => type.Namespace?.StartsWith("Domains.Ordering.", StringComparison.Ordinal) == true)
                .Select(CreateShape),
            configurationDefinitionRegistry: null);

        var entityUnit = catalog.GetUnits<UnitEntity>().Should().ContainSingle().Which;
        var repositoryUnit = catalog.GetUnits<UnitRepository>().Should().ContainSingle().Which;
        repositoryUnit.Type.Name.Should().Be("RepositoryOrder");
        repositoryUnit.RepoInterface.Name.Should().Be("IRepositoryOrder");
        repositoryUnit.RepoInterface.GetInterfaces().Should().Contain(@interface =>
            @interface.IsGenericType && @interface.GetGenericTypeDefinition() == typeof(IRepository<,>));
        repositoryUnit.EntityType.Should().Be(entityUnit.Type);
        repositoryUnit.DependencyUnits.Should().Contain(entityUnit);
        catalog.GetUnits<UnitApplicationService>().Should().HaveCount(3);
        catalog.GetUnits<UnitRequestDto>().Should().HaveCount(2);
        catalog.GetUnits<UnitDomainService>().Should().BeEmpty();
        catalog.GetAllUnits().Should().HaveCount(7);

        var classifier = new ProjectUnitSymbolClassifier();
        foreach (var runtimeUnit in catalog.GetAllUnits())
        {
            var sourceUnit = classifier.Classify(compilation.GetTypeByMetadataName(runtimeUnit.Key)!)!;
            sourceUnit.Should().NotBeNull();
            sourceUnit.UnitType.Should().Be(Enum.Parse<ProjectUnitSourceType>(runtimeUnit.UnitType.ToString()));
            sourceUnit.ExecutionPoints.Should().Equal(runtimeUnit.ExecutionPoints);
            sourceUnit.HasExplicitMetadata.Should().BeTrue();
            sourceUnit.RequirementIds.Should().Equal(REQUIREMENT_ID);
            sourceUnit.Diagnostics.Should().BeEmpty();
            runtimeUnit.MetadataTitle.Should().Be(sourceUnit.Title);
            runtimeUnit.MetadataDescription.Should().Be(sourceUnit.Description).And.NotBeNullOrWhiteSpace();
            runtimeUnit.RequirementIds.Should().Equal(sourceUnit.RequirementIds);
            runtimeUnit.Tags.Should().Equal(sourceUnit.Tags);
            runtimeUnit.Alerts.Should().BeEmpty();
            if (runtimeUnit is UnitApplicationService handler)
            {
                handler.RequestType.Should().NotBeNull();
                handler.DependencyUnits.Should().Contain(unit => unit.Type == handler.RequestType);
                sourceUnit.Dependencies.Should().Contain(dependency =>
                    dependency.Symbol.GetRuntimeName() == handler.RequestType!.FullName);
            }
        }

        var summaryType = assembly.GetType("Domains.Ordering.Models.OrderSummary", throwOnError: true)!;
        classifier.Classify(compilation.GetTypeByMetadataName(summaryType.FullName!)!).Should().BeNull();
        var order = Activator.CreateInstance(entityUnit.Type, 41L, "  ORD-41  ")!;
        entityUnit.Type.GetProperty("Number")!.GetValue(order).Should().Be("ORD-41");
        var probeType = assembly.GetType("TemplateVerification.RepositoryProbe", throwOnError: true)!;
        var repository = Activator.CreateInstance(probeType, order)!;
        var commandHandlerType = assembly.GetType(
            "Domains.Ordering.Application.HandlersCommand.CommandHandlerApproveOrder", throwOnError: true)!;
        var commandType = assembly.GetType(
            "Domains.Ordering.Application.HandlersCommand.CommandApproveOrder", throwOnError: true)!;
        var commandHandler = Activator.CreateInstance(commandHandlerType, repository)!;
        var command = Activator.CreateInstance(commandType, 41L)!;

        var approval = (Res)await InvokeHandlerAsync(commandHandler, command, cancellationToken);
        approval.IsFailed(out _).Should().BeFalse();
        entityUnit.Type.GetProperty("IsApproved")!.GetValue(order).Should().Be(true);
        probeType.GetProperty("LastToken")!.GetValue(repository).Should().Be(cancellationToken);

        Func<Task> repeatedApproval = async () =>
        {
            _ = await InvokeHandlerAsync(commandHandler, command, cancellationToken);
        };
        await repeatedApproval.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Order is already approved.");
        ((IEnumerable<long>)probeType.GetProperty("LoadedIds")!.GetValue(repository)!)
            .Should().Equal(41L, 41L);

        var queryHandlerType = assembly.GetType(
            "Domains.Ordering.Application.HandlersQuery.QueryHandlerGetOrder", throwOnError: true)!;
        var queryType = assembly.GetType(
            "Domains.Ordering.Application.HandlersQuery.QueryGetOrder", throwOnError: true)!;
        var queryHandler = Activator.CreateInstance(queryHandlerType, repository)!;
        catalog.GetUnits<UnitApplicationService>().Single(unit => unit.Type == commandHandlerType)
            .ResponseType.Should().BeNull();
        catalog.GetUnits<UnitApplicationService>().Single(unit => unit.Type == queryHandlerType)
            .ResponseType.Should().Be(summaryType);

        var customHandlerType = assembly.GetType(
            "Domains.Ordering.Application.HandlersQuery.CustomQueryHandlerGetOrder", throwOnError: true)!;
        catalog.GetUnits<UnitApplicationService>().Single(unit => unit.Type == customHandlerType)
            .ResponseType.Should().Be(typeof(Res<>).MakeGenericType(summaryType));
        var customHandler = Activator.CreateInstance(customHandlerType, repository)!;
        var customResult = await InvokeHandlerAsync(
            customHandler, Activator.CreateInstance(queryType, 41L)!, cancellationToken);
        ((IResultEnvelope)customResult).Status.Should().Be(ResStatus.Ok);

        var queryResult = await InvokeHandlerAsync(queryHandler, Activator.CreateInstance(queryType, 41L)!, cancellationToken);
        ((IResultEnvelope)queryResult).Status.Should().Be(ResStatus.Ok);
        var summary = queryResult.GetType().GetProperty("Data")!.GetValue(queryResult)!;
        summary.GetType().Should().Be(summaryType);
        summaryType.GetProperty("Number")!.GetValue(summary).Should().Be("ORD-41");
        summaryType.GetProperty("IsApproved")!.GetValue(summary).Should().Be(true);

        var missingResult = await InvokeHandlerAsync(
            queryHandler, Activator.CreateInstance(queryType, 99L)!, cancellationToken);
        ((IResultEnvelope)missingResult).Status.Should().Be(ResStatus.NotFound);
    }

    private static async Task<object> InvokeHandlerAsync(object handler, object request, CancellationToken cancellationToken)
    {
        var task = (Task)handler.GetType().GetMethod("Handle")!.Invoke(handler, [request, cancellationToken])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static IEnumerable<string> ReadExamples()
    {
        using var resource = typeof(ProjectUnitUseCaseTemplateTests).Assembly.GetManifestResourceStream(
            "ProjectUnitTemplates.ConnectedUseCase.md")
            ?? throw new InvalidOperationException("Connected use-case template resource is missing.");
        using var reader = new StreamReader(resource);
        var blocks = Regex.Matches(reader.ReadToEnd(), @"(?ms)^```csharp\r?\n(?<code>.*?)^```");
        blocks.Should().HaveCount(4);
        return blocks.Select(block => block.Groups["code"].Value.Replace(
            "$RequirementId$", REQUIREMENT_ID, StringComparison.Ordinal)).ToArray();
    }

    private static CSharpCompilation CreateCompilation(IEnumerable<string> sources, CancellationToken cancellationToken)
    {
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var projectAssemblies = new[]
        {
            typeof(ApplicationService<>).Assembly.Location,
            typeof(IRepository<>).Assembly.Location,
            typeof(DbContext).Assembly.Location,
            typeof(Res).Assembly.Location,
            typeof(ProjectUnitMetadataAttribute).Assembly.Location,
            typeof(ICachedServiceProvider).Assembly.Location
        };
        var references = trustedAssemblies.Concat(projectAssemblies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
            "CanonicalProjectUnitUseCaseTemplate",
            sources.Select(source => CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(LanguageVersion.Preview),
                cancellationToken: cancellationToken)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    private static BusinessTypeShape CreateShape(Type type)
        => (BusinessTypeShape)Activator.CreateInstance(
            typeof(BusinessTypeShape),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [type],
            culture: null)!;

    private const string REQUIREMENT_ID = "template-test.order-approval";

    private const string CUSTOM_HANDLER_SOURCE = """
        using System.Threading;
        using System.Threading.Tasks;
        using Monica.Core.Results;
        using Monica.ProjectUnits.Annotations;
        using Monica.Application.Abstractions;
        using Monica.WebApi.Abstractions;
        using Domains.Ordering.Interfaces;
        using Domains.Ordering.Models;

        namespace Domains.Ordering.Application.HandlersQuery;

        [ProjectUnitMetadata("Custom Order Summary",
            Description = "Returns the explicit custom result for the connected order query.",
            Tags = ["ordering", "approval"])]
        [ProjectUnitRequirement("template-test.order-approval")]
        public sealed class CustomQueryHandlerGetOrder(IRepositoryOrder repository)
            : CustomApplicationService<QueryGetOrder, Res<OrderSummary>>
        {
            public override async Task<Res<OrderSummary>> Handle(QueryGetOrder request, CancellationToken cancellationToken)
            {
                var summary = await repository.FindSummaryAsync(request.Id, cancellationToken);
                return summary is null
                    ? Res.Fail("Order was not found.", ResStatus.NotFound)
                    : Res.Ok<OrderSummary>(summary);
            }
        }
        """;

    private const string REPOSITORY_PROBE_SOURCE = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Domains.Ordering.Entities;
        using Domains.Ordering.Interfaces;
        using Domains.Ordering.Models;

        namespace TemplateVerification;

        public sealed class RepositoryProbe(Order order) : IRepositoryOrder
        {
            public List<long> LoadedIds { get; } = [];
            public CancellationToken LastToken { get; private set; }

            public void Add(Order value) => throw new NotSupportedException();
            public void Remove(Order value) => throw new NotSupportedException();

            public Task<Order?> FindAsync(long id, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastToken = cancellationToken;
                LoadedIds.Add(id);
                return Task.FromResult<Order?>(id == order.Id ? order : null);
            }

            public async Task<Order> GetAsync(long id, CancellationToken cancellationToken = default)
                => await FindAsync(id, cancellationToken) ?? throw new InvalidOperationException("Missing order.");

            public Task<OrderSummary?> FindSummaryAsync(long id, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastToken = cancellationToken;
                return Task.FromResult<OrderSummary?>(id == order.Id
                    ? new OrderSummary(order.Id, order.Number, order.IsApproved)
                    : null);
            }
        }
        """;
}
