using Microsoft.Extensions.Logging;
using Monica.EventBus;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Events;
using Monica.Core.TypeDiscovery.Models;
using Monica.ProjectUnits.Services.Support;
using Monica.Modules;
using Monica.Tool.Extensions;

namespace Monica.ProjectUnits.Models;

/// <summary>
/// Describes a distributed domain-event handler and the event project unit that it consumes.
/// </summary>
public class UnitDomainEventHandler : ProjectUnit
{
    internal UnitDomainEventHandler(BusinessTypeShape shape, ProjectUnitCatalog catalog, bool isBatch = false)
        : base(shape, EProjectUnitType.DomainEventHandler, catalog,
            isBatch ? EventBusExecutionPoints.DistributedBatchHandler : EventBusExecutionPoints.DistributedHandler)
    {
    }

    protected override bool ShouldAnalyzeConstructorDependencies => true;

    /// <summary>
    /// Gets the event payload type handled by this distributed event handler.
    /// </summary>
    public Type EventType { get; private set; } = null!;

    protected override ProjectUnitNamingRule? DefaultConventionOption()
    {
        return new ProjectUnitNamingRule
        {
            Prefix = "DomainEventHandler"
        };
    }

    internal static ProjectUnit Create(
        BusinessTypeShape shape,
        ProjectUnitCatalog catalog,
        Type eventType, bool isBatch = false)
    {
        var unit = new UnitDomainEventHandler(shape, catalog, isBatch);
        unit.CheckNameConventionMode();
        unit.EventType = eventType;
        return unit;
    }

    /// <inheritdoc />
    public override void DoingConnect()
    {
        if (Catalog.FindByFullName<UnitDomainEvent>(EventType.FullName) is not { } eventUnit)
        {
            var alertMessage =
                $"Distributed-event handler project unit {this} could not associate handled event type " +
                $"{EventType.GetCleanFullName()} because no event project unit was discovered. Ensure the event " +
                $"implements {nameof(IDomainEvent)} and is included in the host's business-type discovery.";
            Alerts.Add(new ProjectUnitAlert
            {
                Level = EAlertLevel.Warning,
                Message = alertMessage,
                Source = "EventTypeAssociation"
            });
            Logger.LogWarning("{EventTypeAssociationAlert}", alertMessage);
            base.DoingConnect();
            return;
        }

        DeclareRelevance(eventUnit, true);
        eventUnit.DeclareRelevance(this);
        base.DoingConnect();
    }
}
