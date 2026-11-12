using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Monica.BuildingBlocks.CodeAnalysis.Models;

namespace Monica.BuildingBlocks.CodeAnalysis.Services;

internal sealed class BuildingBlockSymbolClassifier
{
    private const string APPLICATION_SERVICE = "Monica.Application.Abstractions.ApplicationService";
    private const string APPLICATION_REQUEST_HANDLER = "Monica.Application.Abstractions.ApplicationRequestHandler`2";
    private const string DISTRIBUTED_EVENT_HANDLER_EXECUTION_POINT = "eventbus.distributed-handler";
    private const string CONTROLLER_BASE = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string CONFIGURATION_ATTRIBUTE = "Monica.Configuration.Annotations.ConfigurationAttribute";
    private const string CRUD_APPLICATION_SERVICE = "Monica.WebApi.AutoControllers.Abstractions.ICrudApplicationService";
    private const string DISTRIBUTED_EVENT_HANDLER = "Monica.EventBus.Abstractions.Handlers.IDistributedEventHandler`1";
    private const string DISTRIBUTED_BATCH_EVENT_HANDLER = "Monica.EventBus.Abstractions.Handlers.IDistributedBatchEventHandler`1";
    private const string DOMAIN_EVENT = "Monica.EventBus.Events.IDomainEvent";
    private const string DOMAIN_EVENT_BASE = "Monica.EventBus.Events.DomainEvent";
    private const string DOMAIN_SERVICE = "Monica.Application.Abstractions.DomainService";
    private const string ENTITY = "Monica.Repository.Entity.Abstractions.IEntity";
    private const string EXCLUDE_FROM_BUSINESS_TYPE_DISCOVERY_ATTRIBUTE =
        "Monica.Core.Modularity.Annotations.ExcludeFromBusinessTypeDiscoveryAttribute";
    private const string HOSTED_SERVICE = "Microsoft.Extensions.Hosting.IHostedService";
    private const string HOSTED_SERVICE_START_EXECUTION_POINT = "hosted-service.start";
    private const string HOSTED_SERVICE_STOP_EXECUTION_POINT = "hosted-service.stop";
    private const string HOSTED_SERVICE_WORK_ITEM_EXECUTION_POINT = "hosted-service.work-item";
    private const string LOCAL_EVENT_HANDLER_EXECUTION_POINT = "eventbus.local-handler";
    private const string MEDIATED_CONTROLLER_ATTRIBUTE = "Monica.Core.Execution.Mvc.MediatedControllerAttribute";
    private const string MEDIATOR_REQUEST_EXECUTION_POINT = "mediator.request";
    private const string MO_BACKGROUND_SERVICE = "Monica.Core.HostedService.Abstractions.MoBackgroundService";
    private const string MO_HOSTED_SERVICE = "Monica.Core.HostedService.Abstractions.MoHostedService";
    private const string MVC_ACTION_EXECUTION_POINT = "webapi.mvc-action";
    private const string RECURRING_JOB_EXECUTION_POINT = "jobs.recurring-attempt";
    private const string SEEDER = "Monica.Framework.Seeder.Abstractions.ISeeder";
    private const string SEEDER_RUN_EXECUTION_POINT = "seeder.run";
    private const string LOCAL_EVENT_HANDLER = "Monica.EventBus.Abstractions.Handlers.ILocalEventHandler`1";
    private const string METADATA_ATTRIBUTE = "Monica.BuildingBlocks.Annotations.BuildingBlockMetadataAttribute";
    private const string RECURRING_JOB = "Monica.JobScheduler.Abstractions.RecurringJob";
    private const string REPOSITORY = "Monica.Repository.Persistence.Abstractions.IRepository`1";
    private const string REQUEST = "Monica.Application.Abstractions.IResultRequestBase";
    private const string NATIVE_REQUEST = "Monica.Core.Mediator.IRequest`1";
    private const string REQUIREMENT_ATTRIBUTE = "Monica.BuildingBlocks.Annotations.BuildingBlockRequirementAttribute";
    private const string TRIGGERED_JOB = "Monica.JobScheduler.Abstractions.TriggeredJob`1";
    private const string TRIGGERED_JOB_EXECUTION_POINT = "jobs.triggered-attempt";
    private const string OPTIONS = "Microsoft.Extensions.Options.IOptions`1";
    private const string OPTIONS_MONITOR = "Microsoft.Extensions.Options.IOptionsMonitor`1";
    private const string OPTIONS_SNAPSHOT = "Microsoft.Extensions.Options.IOptionsSnapshot`1";

    internal BuildingBlockSymbolCandidate? Classify(INamedTypeSymbol symbol)
    {
        if (symbol is not { TypeKind: TypeKind.Class, IsAbstract: false, Arity: 0 })
        {
            return null;
        }

        if (symbol.FindAttribute(EXCLUDE_FROM_BUSINESS_TYPE_DISCOVERY_ATTRIBUTE, inherit: false) is not null)
        {
            return null;
        }

        var classification = ClassifyType(symbol);
        if (classification is null)
        {
            return null;
        }

        if (!BuildingBlockSourceAnalysisContract.DiscoverableKinds.Contains(classification.Kind))
        {
            throw new InvalidOperationException(
                $"Source classifier returned an undeclared building-block kind: {classification.Kind}.");
        }

        var diagnostics = new List<BuildingBlockSourceDiagnostic>();
        var description = ReadDocumentationSummary(symbol);
        var title = symbol.Name;

        if (symbol.FindAttribute(CONFIGURATION_ATTRIBUTE, inherit: false) is { } configuration)
        {
            title = configuration.ReadNamedString("DisplayName") ?? title;
            description = configuration.ReadNamedString("Description") ?? description;
        }

        var metadataAttributes = symbol.GetAttributes()
            .Where(attribute => string.Equals(
                attribute.AttributeClass?.GetMetadataName(),
                METADATA_ATTRIBUTE,
                StringComparison.Ordinal))
            .ToArray();
        var hasExplicitMetadata = metadataAttributes.Length != 0;
        string? owner = null;
        IReadOnlyList<string> tags = [];
        if (metadataAttributes.Length > 1)
        {
            diagnostics.Add(Warning(
                "BuildingBlock.Metadata.Duplicate",
                "A building block may declare only one BuildingBlockMetadataAttribute."));
        }

        if (metadataAttributes.FirstOrDefault() is { } metadata)
        {
            if (metadata.ReadString(0) is { } metadataTitle)
            {
                title = metadataTitle;
            }
            else
            {
                diagnostics.Add(Warning(
                    "BuildingBlock.Metadata.Title.Empty",
                    "Building-block metadata title must not be empty."));
            }

            owner = metadata.ReadNamedString("Owner");
            description = metadata.ReadNamedString("Description") ?? description;
            tags = NormalizeValues(
                metadata.ReadNamedStrings("Tags"),
                "BuildingBlock.Metadata.Tag.Empty",
                "Building-block metadata tags must not contain empty values.",
                diagnostics);
        }

        var requirements = NormalizeValues(
            symbol.GetAttributes()
                .Where(attribute => string.Equals(
                    attribute.AttributeClass?.GetMetadataName(),
                    REQUIREMENT_ATTRIBUTE,
                    StringComparison.Ordinal))
                .Select(attribute => attribute.ReadString(0)),
            "BuildingBlock.Requirement.Id.Empty",
            "Building-block requirement identifiers must not be empty.",
            diagnostics);

        var dependencies = BuildDependencies(symbol, classification);
        return new BuildingBlockSymbolCandidate(
            symbol,
            classification.Kind,
            classification.ExecutionPoints,
            title,
            description,
            owner,
            tags,
            requirements,
            hasExplicitMetadata,
            dependencies,
            diagnostics);
    }

    private static BuildingBlockTypeClassification? ClassifyType(INamedTypeSymbol symbol)
    {
        var isApplicationService = symbol.DerivesFrom(APPLICATION_SERVICE);
        if (isApplicationService && symbol.FindInterface(CRUD_APPLICATION_SERVICE) is not null)
        {
            return new(
                SourceBuildingBlockKind.CrudApplicationService,
                [MVC_ACTION_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindBaseType(APPLICATION_REQUEST_HANDLER) is not null)
        {
            return new(
                SourceBuildingBlockKind.ApplicationRequestHandler,
                [MEDIATOR_REQUEST_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (symbol.DerivesFrom(CONTROLLER_BASE)
            && symbol.FindInterface(CRUD_APPLICATION_SERVICE) is null)
        {
            var executionPoints = symbol.FindAttribute(MEDIATED_CONTROLLER_ATTRIBUTE) is null
                ? new[] { MVC_ACTION_EXECUTION_POINT }
                : [];
            return new(SourceBuildingBlockKind.HttpApi, executionPoints, AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindAttribute(CONFIGURATION_ATTRIBUTE, inherit: false) is not null)
        {
            return new(SourceBuildingBlockKind.Configuration, [], AnalyzeConstructorDependencies: false);
        }

        if (symbol.FindInterface(DISTRIBUTED_EVENT_HANDLER) is not null)
        {
            return new(
                SourceBuildingBlockKind.DistributedEventHandler,
                [DISTRIBUTED_EVENT_HANDLER_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindInterface(DISTRIBUTED_BATCH_EVENT_HANDLER) is not null)
        {
            return new(SourceBuildingBlockKind.DistributedEventHandler, ["eventbus.distributed-batch-handler"],
                AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindInterface(LOCAL_EVENT_HANDLER) is not null)
        {
            return new(
                SourceBuildingBlockKind.LocalEventHandler,
                [LOCAL_EVENT_HANDLER_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (!string.Equals(symbol.GetMetadataName(), DOMAIN_EVENT_BASE, StringComparison.Ordinal)
            && symbol.FindInterface(DOMAIN_EVENT) is not null)
        {
            return new(SourceBuildingBlockKind.DomainEvent, [], AnalyzeConstructorDependencies: false);
        }

        if (symbol.FindInterface(REPOSITORY) is not null
            && symbol.AllInterfaces.Any(candidate => candidate.Name == $"I{symbol.Name}"))
        {
            return new(SourceBuildingBlockKind.Repository, [], AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindInterface(ENTITY) is not null)
        {
            return new(SourceBuildingBlockKind.Entity, [], AnalyzeConstructorDependencies: false);
        }

        if (symbol.DerivesFrom(DOMAIN_SERVICE))
        {
            return new(SourceBuildingBlockKind.DomainService, [], AnalyzeConstructorDependencies: true);
        }

        if (symbol.DerivesFrom(RECURRING_JOB))
        {
            return new(
                SourceBuildingBlockKind.RecurringJob,
                [RECURRING_JOB_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindBaseType(TRIGGERED_JOB) is not null)
        {
            return new(
                SourceBuildingBlockKind.TriggeredJob,
                [TRIGGERED_JOB_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindInterface(SEEDER) is not null)
        {
            return new(
                SourceBuildingBlockKind.Seeder,
                [SEEDER_RUN_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindInterface(HOSTED_SERVICE) is not null)
        {
            var executionPoints = symbol.DerivesFrom(MO_HOSTED_SERVICE)
                                  || symbol.DerivesFrom(MO_BACKGROUND_SERVICE)
                ? new[]
                {
                    HOSTED_SERVICE_START_EXECUTION_POINT,
                    HOSTED_SERVICE_STOP_EXECUTION_POINT,
                    HOSTED_SERVICE_WORK_ITEM_EXECUTION_POINT
                }
                : [];
            return new(SourceBuildingBlockKind.HostedService, executionPoints, AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindInterface(REQUEST) is not null || symbol.FindInterface(NATIVE_REQUEST) is not null)
        {
            return new(SourceBuildingBlockKind.RequestDto, [], AnalyzeConstructorDependencies: false);
        }

        return null;
    }

    private static IReadOnlyList<BuildingBlockSymbolDependency> BuildDependencies(
        INamedTypeSymbol symbol,
        BuildingBlockTypeClassification classification)
    {
        var dependencies = new List<BuildingBlockSymbolDependency>();
        switch (classification.Kind)
        {
            case SourceBuildingBlockKind.ApplicationRequestHandler:
                var requestHandlerBase = symbol.FindBaseType(APPLICATION_REQUEST_HANDLER);
                if (requestHandlerBase?.TypeArguments.FirstOrDefault() is INamedTypeSymbol request)
                {
                    dependencies.Add(new(request, "BuildingBlock.ApplicationRequestHandler.Request.NotFound"));
                }
                break;
            case SourceBuildingBlockKind.DistributedEventHandler:
                if ((symbol.FindInterface(DISTRIBUTED_EVENT_HANDLER) ?? symbol.FindInterface(DISTRIBUTED_BATCH_EVENT_HANDLER))
                    ?.TypeArguments.FirstOrDefault() is INamedTypeSymbol distributedEvent)
                {
                    dependencies.Add(new(distributedEvent, "BuildingBlock.EventHandler.Event.NotFound"));
                }
                break;
            case SourceBuildingBlockKind.LocalEventHandler:
                if (symbol.FindInterface(LOCAL_EVENT_HANDLER)?.TypeArguments.FirstOrDefault() is INamedTypeSymbol localEvent)
                {
                    dependencies.Add(new(localEvent, "BuildingBlock.EventHandler.Event.NotFound"));
                }
                break;
            case SourceBuildingBlockKind.Repository:
                if (symbol.FindInterface(REPOSITORY)?.TypeArguments.FirstOrDefault() is INamedTypeSymbol entity)
                {
                    dependencies.Add(new(entity, "BuildingBlock.Repository.Entity.NotFound"));
                }
                break;
        }

        if (classification.AnalyzeConstructorDependencies)
        {
            foreach (var constructor in symbol.InstanceConstructors.Where(static constructor =>
                         constructor.DeclaredAccessibility == Accessibility.Public))
            {
                foreach (var parameter in constructor.Parameters)
                {
                    if (ResolveOptionsType(parameter.Type) is { } dependency)
                    {
                        dependencies.Add(new(dependency, null));
                    }
                }
            }
        }

        return dependencies
            .DistinctBy(static dependency => SymbolIdentity.Create(dependency.Symbol), StringComparer.Ordinal)
            .ToArray();
    }

    private static INamedTypeSymbol? ResolveOptionsType(ITypeSymbol symbol)
    {
        if (symbol is not INamedTypeSymbol named)
        {
            return null;
        }

        var definition = named.OriginalDefinition.GetMetadataName();
        if (definition is OPTIONS or OPTIONS_MONITOR or OPTIONS_SNAPSHOT)
        {
            return named.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
        }

        return named;
    }

    private static IReadOnlyList<string> NormalizeValues(
        IEnumerable<string?> values,
        string diagnosticCode,
        string diagnosticMessage,
        ICollection<BuildingBlockSourceDiagnostic> diagnostics)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (BuildingBlockSymbolFacts.Normalize(value) is not { } normalized)
            {
                diagnostics.Add(Warning(diagnosticCode, diagnosticMessage));
            }
            else if (seen.Add(normalized))
            {
                results.Add(normalized);
            }
        }

        return results;
    }

    private static string? ReadDocumentationSummary(INamedTypeSymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: default);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var summary = XElement.Parse(xml).Element("summary")?.Value;
            return BuildingBlockSymbolFacts.Normalize(
                summary is null
                    ? null
                    : string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static BuildingBlockSourceDiagnostic Warning(string code, string message)
        => new(code, BuildingBlockSourceDiagnosticSeverity.Warning, message);

    private sealed record BuildingBlockTypeClassification(
        SourceBuildingBlockKind Kind,
        IReadOnlyList<string> ExecutionPoints,
        bool AnalyzeConstructorDependencies);
}

internal sealed record BuildingBlockSymbolCandidate(
    INamedTypeSymbol Symbol,
    SourceBuildingBlockKind Kind,
    IReadOnlyList<string> ExecutionPoints,
    string Title,
    string? Description,
    string? Owner,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> RequirementIds,
    bool HasExplicitMetadata,
    IReadOnlyList<BuildingBlockSymbolDependency> Dependencies,
    IReadOnlyList<BuildingBlockSourceDiagnostic> Diagnostics);

internal sealed record BuildingBlockSymbolDependency(
    INamedTypeSymbol Symbol,
    string? MissingDiagnosticCode);

internal static class SymbolIdentity
{
    internal static string Create(INamedTypeSymbol symbol)
        => $"{symbol.ContainingAssembly.Identity.Name}::{symbol.GetRuntimeName()}";
}
