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
using Monica.BuildingBlocks.Models;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Abstractions;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Monica.WebApi.AutoControllers.Abstractions;

namespace Monica.BuildingBlocks.Services.Support;

/// <summary>
/// Classifies one shared business-type shape by building-block priority before constructing the selected model.
/// </summary>
internal sealed class BuildingBlockFactory(BuildingBlockCatalog catalog)
{
    /// <summary>
    /// Creates at most one building-block model for <paramref name="shape"/>.
    /// </summary>
    internal BuildingBlock? Create(BusinessTypeShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        if (!IsCandidate(shape))
        {
            return null;
        }

        // Ordering is semantic: more specific categories must claim a type before their broader alternatives.
        if (shape.IsSubclassOf(typeof(ApplicationService)) && shape.IsAssignableTo(typeof(ICrudApplicationService)))
        {
            return CrudApplicationServiceBuildingBlock.Create(shape, catalog);
        }

        if (FindGenericBase(shape, typeof(ApplicationRequestHandler<,>)) is { } requestHandler)
        {
            return ApplicationRequestHandlerBuildingBlock.Create(shape, catalog, requestHandler);
        }

        if (shape.IsAssignableTo(typeof(ControllerBase))
            && !shape.IsAssignableTo(typeof(ICrudApplicationService)))
        {
            return HttpApiBuildingBlock.Create(shape, catalog);
        }

        if (shape.HasAttribute(typeof(ConfigurationAttribute), inherit: false)
            && shape.GetAttribute<ConfigurationAttribute>() is { } configuration)
        {
            return ConfigurationBuildingBlock.Create(shape, catalog, configuration);
        }

        if (FindInterface(shape, typeof(IDistributedEventHandler<>)) is { ClosedInterface.FullName: not null } distributedHandler)
        {
            return DistributedEventHandlerBuildingBlock.Create(shape, catalog, distributedHandler.GenericArguments[0]);
        }

        if (FindInterface(shape, typeof(IDistributedBatchEventHandler<>)) is { ClosedInterface.FullName: not null } batchHandler)
        {
            return DistributedEventHandlerBuildingBlock.Create(shape, catalog, batchHandler.GenericArguments[0], isBatch: true);
        }

        if (FindInterface(shape, typeof(ILocalEventHandler<>)) is { ClosedInterface.FullName: not null } localHandler)
        {
            return LocalEventHandlerBuildingBlock.Create(shape, catalog, localHandler.GenericArguments[0]);
        }

        if (shape.Type != typeof(DomainEvent) && shape.IsAssignableTo(typeof(IDomainEvent)))
        {
            return DomainEventBuildingBlock.Create(shape, catalog);
        }

        if (FindInterface(shape, typeof(IRepository<>)) is { } repository)
        {
            var repositoryInterface = FindRepositoryInterface(shape);
            if (repositoryInterface is not null)
            {
                return RepositoryBuildingBlock.Create(
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
            return EntityBuildingBlock.Create(shape, catalog);
        }

        if (shape.IsSubclassOf(typeof(DomainService)))
        {
            return DomainServiceBuildingBlock.Create(shape, catalog);
        }

        if (shape.IsSubclassOf(typeof(RecurringJob)))
        {
            return RecurringJobBuildingBlock.Create(shape, catalog);
        }

        if (FindGenericBase(shape, typeof(TriggeredJob<>)) is { } triggeredJob)
        {
            return TriggeredJobBuildingBlock.Create(shape, catalog, triggeredJob.GetGenericArguments()[0]);
        }

        if (shape.IsAssignableTo(typeof(ISeeder)))
        {
            return SeederBuildingBlock.Create(shape, catalog);
        }

        if (shape.IsAssignableTo(typeof(IHostedService)))
        {
            return HostedServiceBuildingBlock.Create(shape, catalog);
        }

        return shape.IsAssignableTo(typeof(IResultRequestBase)) || FindInterface(shape, typeof(IRequest<>)) is not null
            ? RequestDtoBuildingBlock.Create(shape, catalog)
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
