using Microsoft.Extensions.Logging;
using Monica.EventBus;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Events;
using Monica.Core.TypeDiscovery.Models;
using Monica.BuildingBlocks.Services.Support;
using Monica.Modules;
using Monica.Tool.Extensions;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Describes a local event handler and the event building block that it consumes.
/// </summary>
public class LocalEventHandlerBuildingBlock : BuildingBlock
{
    internal LocalEventHandlerBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.LocalEventHandler, catalog, EventBusExecutionPoints.LocalHandler)
    {
    }

    protected override bool ShouldAnalyzeConstructorDependencies => true;

    /// <summary>
    /// Gets the event payload type handled by this local event handler.
    /// </summary>
    public Type EventType { get; private set; } = null!;

    protected override BuildingBlockNamingRule? DefaultConventionOption()
    {
        return new BuildingBlockNamingRule
        {
            Prefix = "LocalEventHandler"
        };
    }

    internal static BuildingBlock Create(
        BusinessTypeShape shape,
        BuildingBlockCatalog catalog,
        Type eventType)
    {
        var unit = new LocalEventHandlerBuildingBlock(shape, catalog);
        unit.CheckMode();
        unit.EventType = eventType;
        return unit;
    }

    /// <inheritdoc />
    public override void DoingConnect()
    {
        if (Catalog.FindByFullName<DomainEventBuildingBlock>(EventType.FullName) is not { } eventUnit)
        {
            var alertMessage =
                $"Local-event handler building block {this} could not associate handled event type " +
                $"{EventType.GetCleanFullName()} because no event building block was discovered. Ensure the event " +
                $"implements {nameof(IDomainEvent)} and is included in the host's business-type discovery.";
            Alerts.Add(new BuildingBlockAlert
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
