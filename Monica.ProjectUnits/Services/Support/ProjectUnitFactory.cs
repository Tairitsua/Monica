using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monica.Configuration.Annotations;
using Monica.Core.TypeDiscovery.Models;
using Monica.Core.Mediator;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Events;
using Monica.Framework.Seeder.Abstractions;
using Monica.JobScheduler;
using Monica.JobScheduler.Abstractions;
using Monica.ProjectUnits.Models;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Abstractions;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Monica.WebApi.AutoControllers.Abstractions;

namespace Monica.ProjectUnits.Services.Support;

/// <summary>
/// Classifies one shared business-type shape by project-unit priority before constructing the selected model.
/// </summary>
internal sealed class ProjectUnitFactory(ProjectUnitCatalog catalog)
{
    /// <summary>
    /// Creates at most one project-unit model for <paramref name="shape"/>.
    /// </summary>
    internal ProjectUnit? Create(BusinessTypeShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        if (!IsCandidate(shape))
        {
            return null;
        }

        // Ordering is semantic: more specific categories must claim a type before their broader alternatives.
        if (shape.IsSubclassOf(typeof(ApplicationService)))
        {
            return shape.IsAssignableTo(typeof(ICrudApplicationService))
                ? UnitCrudApplicationService.Create(shape, catalog)
                : UnitApplicationService.Create(shape, catalog);
        }

        if (shape.IsAssignableTo(typeof(ControllerBase))
            && !shape.IsAssignableTo(typeof(ICrudApplicationService)))
        {
            return UnitHttpApi.Create(shape, catalog);
        }

        if (shape.HasAttribute(typeof(ConfigurationAttribute), inherit: false)
            && shape.GetAttribute<ConfigurationAttribute>() is { } configuration)
        {
            return UnitConfiguration.Create(shape, catalog, configuration);
        }

        if (FindInterface(shape, typeof(IDistributedEventHandler<>)) is { ClosedInterface.FullName: not null } distributedHandler)
        {
            return UnitDomainEventHandler.Create(shape, catalog, distributedHandler.GenericArguments[0]);
        }

        if (FindInterface(shape, typeof(ILocalEventHandler<>)) is { ClosedInterface.FullName: not null } localHandler)
        {
            return UnitLocalEventHandler.Create(shape, catalog, localHandler.GenericArguments[0]);
        }

        if (shape.Type != typeof(DomainEvent) && shape.IsAssignableTo(typeof(IDomainEvent)))
        {
            return UnitDomainEvent.Create(shape, catalog);
        }

        if (FindInterface(shape, typeof(IRepository<>)) is { } repository)
        {
            var repositoryInterface = FindRepositoryInterface(shape);
            if (repositoryInterface is not null)
            {
                return UnitRepository.Create(
                    shape,
                    catalog,
                    repository.GenericArguments[0],
                    repositoryInterface);
            }

            catalog.Logger.LogError(
                "Repository {RepositoryType} was discovered, but its expected interface I{RepositoryType} was not found.",
                shape.Type.Name,
                shape.Type.Name);
        }

        if (shape.IsAssignableTo(typeof(IEntity)))
        {
            return UnitEntity.Create(shape, catalog);
        }

        if (shape.IsSubclassOf(typeof(DomainService)))
        {
            return UnitDomainService.Create(shape, catalog);
        }

        if (shape.IsSubclassOf(typeof(RecurringJob)))
        {
            return UnitRecurringJob.Create(shape, catalog);
        }

        if (FindGenericBase(shape, typeof(TriggeredJob<>)) is { } triggeredJob)
        {
            return UnitTriggeredJob.Create(shape, catalog, triggeredJob.GetGenericArguments()[0]);
        }

        if (shape.IsAssignableTo(typeof(ISeeder)))
        {
            return UnitSeeder.Create(shape, catalog);
        }

        if (shape.IsAssignableTo(typeof(IHostedService)))
        {
            return UnitHostedService.Create(shape, catalog);
        }

        return shape.IsAssignableTo(typeof(IResultRequestBase)) || FindInterface(shape, typeof(IRequest<>)) is not null
            ? UnitRequestDto.Create(shape, catalog)
            : null;
    }

    private static bool IsCandidate(BusinessTypeShape shape)
    {
        return shape.IsConcreteClass
               && shape.Type.FullName is not null
               && shape.IsClosedType;
    }

    private static OpenGenericInterfaceMatch? FindInterface(
        BusinessTypeShape shape,
        Type openGenericInterface)
    {
        return shape.GetOpenGenericInterfaceMatches(openGenericInterface).FirstOrDefault();
    }

    private static Type? FindGenericBase(BusinessTypeShape shape, Type openGenericBase)
    {
        return shape.BaseTypes.FirstOrDefault(type =>
            type.IsGenericType && type.GetGenericTypeDefinition() == openGenericBase);
    }

    private static Type? FindRepositoryInterface(BusinessTypeShape shape)
    {
        var expectedName = $"I{shape.Type.Name}";
        return shape.Interfaces.SingleOrDefault(type => type.Name == expectedName);
    }
}
