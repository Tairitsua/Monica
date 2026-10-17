using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Monica.ProjectUnits.CodeAnalysis.Models;

namespace Monica.ProjectUnits.CodeAnalysis.Services;

internal sealed class ProjectUnitSymbolClassifier
{
    private const string APPLICATION_SERVICE = "Monica.Application.Abstractions.ApplicationService";
    private const string APPLICATION_SERVICE_GENERIC = "Monica.Application.Abstractions.ApplicationService`2";
    private const string APPLICATION_SERVICE_WITHOUT_RESPONSE = "Monica.Application.Abstractions.ApplicationService`1";
    private const string CUSTOM_APPLICATION_SERVICE_GENERIC = "Monica.Application.Abstractions.CustomApplicationService`2";
    private const string DISTRIBUTED_EVENT_HANDLER_EXECUTION_POINT = "eventbus.distributed-handler";
    private const string CONTROLLER_BASE = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string CONFIGURATION_ATTRIBUTE = "Monica.Configuration.Annotations.ConfigurationAttribute";
    private const string CRUD_APPLICATION_SERVICE = "Monica.WebApi.AutoControllers.Abstractions.ICrudApplicationService";
    private const string DISTRIBUTED_EVENT_HANDLER = "Monica.EventBus.Abstractions.Handlers.IDistributedEventHandler`1";
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
    private const string METADATA_ATTRIBUTE = "Monica.ProjectUnits.Annotations.ProjectUnitMetadataAttribute";
    private const string RECURRING_JOB = "Monica.JobScheduler.Abstractions.RecurringJob";
    private const string REPOSITORY = "Monica.Repository.Persistence.Abstractions.IRepository`1";
    private const string REQUEST = "Monica.Application.Abstractions.IResultRequestBase";
    private const string REQUIREMENT_ATTRIBUTE = "Monica.ProjectUnits.Annotations.ProjectUnitRequirementAttribute";
    private const string TRIGGERED_JOB = "Monica.JobScheduler.Abstractions.TriggeredJob`1";
    private const string TRIGGERED_JOB_EXECUTION_POINT = "jobs.triggered-attempt";
    private const string OPTIONS = "Microsoft.Extensions.Options.IOptions`1";
    private const string OPTIONS_MONITOR = "Microsoft.Extensions.Options.IOptionsMonitor`1";
    private const string OPTIONS_SNAPSHOT = "Microsoft.Extensions.Options.IOptionsSnapshot`1";

    internal ProjectUnitSymbolCandidate? Classify(INamedTypeSymbol symbol)
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

        if (!ProjectUnitSourceAnalysisContract.DiscoverableUnitTypes.Contains(classification.UnitType))
        {
            throw new InvalidOperationException(
                $"Source classifier returned an undeclared ProjectUnit role: {classification.UnitType}.");
        }

        var diagnostics = new List<ProjectUnitSourceDiagnostic>();
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
                "ProjectUnit.Metadata.Duplicate",
                "A ProjectUnit may declare only one ProjectUnitMetadataAttribute."));
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
                    "ProjectUnit.Metadata.Title.Empty",
                    "Project-unit metadata title must not be empty."));
            }

            owner = metadata.ReadNamedString("Owner");
            description = metadata.ReadNamedString("Description") ?? description;
            tags = NormalizeValues(
                metadata.ReadNamedStrings("Tags"),
                "ProjectUnit.Metadata.Tag.Empty",
                "Project-unit metadata tags must not contain empty values.",
                diagnostics);
        }

        var requirements = NormalizeValues(
            symbol.GetAttributes()
                .Where(attribute => string.Equals(
                    attribute.AttributeClass?.GetMetadataName(),
                    REQUIREMENT_ATTRIBUTE,
                    StringComparison.Ordinal))
                .Select(attribute => attribute.ReadString(0)),
            "ProjectUnit.Requirement.Id.Empty",
            "Project-unit requirement identifiers must not be empty.",
            diagnostics);

        var dependencies = BuildDependencies(symbol, classification);
        return new ProjectUnitSymbolCandidate(
            symbol,
            classification.UnitType,
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

    private static ProjectUnitTypeClassification? ClassifyType(INamedTypeSymbol symbol)
    {
        var isApplicationService = symbol.DerivesFrom(APPLICATION_SERVICE);
        if (isApplicationService && symbol.FindInterface(CRUD_APPLICATION_SERVICE) is not null)
        {
            return new(
                ProjectUnitSourceType.CrudApplicationService,
                [MVC_ACTION_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (isApplicationService)
        {
            return new(
                ProjectUnitSourceType.ApplicationService,
                [MEDIATOR_REQUEST_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (symbol.DerivesFrom(CONTROLLER_BASE)
            && symbol.FindInterface(CRUD_APPLICATION_SERVICE) is null)
        {
            var executionPoints = symbol.FindAttribute(MEDIATED_CONTROLLER_ATTRIBUTE) is null
                ? new[] { MVC_ACTION_EXECUTION_POINT }
                : [];
            return new(ProjectUnitSourceType.HttpApi, executionPoints, AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindAttribute(CONFIGURATION_ATTRIBUTE, inherit: false) is not null)
        {
            return new(ProjectUnitSourceType.Configuration, [], AnalyzeConstructorDependencies: false);
        }

        if (symbol.FindInterface(DISTRIBUTED_EVENT_HANDLER) is not null)
        {
            return new(
                ProjectUnitSourceType.DomainEventHandler,
                [DISTRIBUTED_EVENT_HANDLER_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindInterface(LOCAL_EVENT_HANDLER) is not null)
        {
            return new(
                ProjectUnitSourceType.LocalEventHandler,
                [LOCAL_EVENT_HANDLER_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (!string.Equals(symbol.GetMetadataName(), DOMAIN_EVENT_BASE, StringComparison.Ordinal)
            && symbol.FindInterface(DOMAIN_EVENT) is not null)
        {
            return new(ProjectUnitSourceType.DomainEvent, [], AnalyzeConstructorDependencies: false);
        }

        if (symbol.FindInterface(REPOSITORY) is not null
            && symbol.AllInterfaces.Any(candidate => candidate.Name == $"I{symbol.Name}"))
        {
            return new(ProjectUnitSourceType.Repository, [], AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindInterface(ENTITY) is not null)
        {
            return new(ProjectUnitSourceType.Entity, [], AnalyzeConstructorDependencies: false);
        }

        if (symbol.DerivesFrom(DOMAIN_SERVICE))
        {
            return new(ProjectUnitSourceType.DomainService, [], AnalyzeConstructorDependencies: true);
        }

        if (symbol.DerivesFrom(RECURRING_JOB))
        {
            return new(
                ProjectUnitSourceType.RecurringJob,
                [RECURRING_JOB_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindBaseType(TRIGGERED_JOB) is not null)
        {
            return new(
                ProjectUnitSourceType.TriggeredJob,
                [TRIGGERED_JOB_EXECUTION_POINT],
                AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindInterface(SEEDER) is not null)
        {
            return new(
                ProjectUnitSourceType.Seeder,
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
            return new(ProjectUnitSourceType.HostedService, executionPoints, AnalyzeConstructorDependencies: true);
        }

        if (symbol.FindInterface(REQUEST) is not null)
        {
            return new(ProjectUnitSourceType.RequestDto, [], AnalyzeConstructorDependencies: false);
        }

        return null;
    }

    private static IReadOnlyList<ProjectUnitSymbolDependency> BuildDependencies(
        INamedTypeSymbol symbol,
        ProjectUnitTypeClassification classification)
    {
        var dependencies = new List<ProjectUnitSymbolDependency>();
        switch (classification.UnitType)
        {
            case ProjectUnitSourceType.ApplicationService:
            case ProjectUnitSourceType.CrudApplicationService:
                var applicationServiceBase = symbol.FindBaseType(APPLICATION_SERVICE_GENERIC)
                    ?? symbol.FindBaseType(APPLICATION_SERVICE_WITHOUT_RESPONSE)
                    ?? symbol.FindBaseType(CUSTOM_APPLICATION_SERVICE_GENERIC);
                if (applicationServiceBase?.TypeArguments.FirstOrDefault() is INamedTypeSymbol request)
                {
                    dependencies.Add(new(request, "ProjectUnit.ApplicationService.Request.NotFound"));
                }
                break;
            case ProjectUnitSourceType.DomainEventHandler:
                if (symbol.FindInterface(DISTRIBUTED_EVENT_HANDLER)?.TypeArguments.FirstOrDefault() is INamedTypeSymbol distributedEvent)
                {
                    dependencies.Add(new(distributedEvent, "ProjectUnit.EventHandler.Event.NotFound"));
                }
                break;
            case ProjectUnitSourceType.LocalEventHandler:
                if (symbol.FindInterface(LOCAL_EVENT_HANDLER)?.TypeArguments.FirstOrDefault() is INamedTypeSymbol localEvent)
                {
                    dependencies.Add(new(localEvent, "ProjectUnit.EventHandler.Event.NotFound"));
                }
                break;
            case ProjectUnitSourceType.Repository:
                if (symbol.FindInterface(REPOSITORY)?.TypeArguments.FirstOrDefault() is INamedTypeSymbol entity)
                {
                    dependencies.Add(new(entity, "ProjectUnit.Repository.Entity.NotFound"));
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
        ICollection<ProjectUnitSourceDiagnostic> diagnostics)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (ProjectUnitSymbolFacts.Normalize(value) is not { } normalized)
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
            return ProjectUnitSymbolFacts.Normalize(
                summary is null
                    ? null
                    : string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static ProjectUnitSourceDiagnostic Warning(string code, string message)
        => new(code, ProjectUnitSourceDiagnosticSeverity.Warning, message);

    private sealed record ProjectUnitTypeClassification(
        ProjectUnitSourceType UnitType,
        IReadOnlyList<string> ExecutionPoints,
        bool AnalyzeConstructorDependencies);
}

internal sealed record ProjectUnitSymbolCandidate(
    INamedTypeSymbol Symbol,
    ProjectUnitSourceType UnitType,
    IReadOnlyList<string> ExecutionPoints,
    string Title,
    string? Description,
    string? Owner,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> RequirementIds,
    bool HasExplicitMetadata,
    IReadOnlyList<ProjectUnitSymbolDependency> Dependencies,
    IReadOnlyList<ProjectUnitSourceDiagnostic> Diagnostics);

internal sealed record ProjectUnitSymbolDependency(
    INamedTypeSymbol Symbol,
    string? MissingDiagnosticCode);

internal static class SymbolIdentity
{
    internal static string Create(INamedTypeSymbol symbol)
        => $"{symbol.ContainingAssembly.Identity.Name}::{symbol.GetRuntimeName()}";
}
