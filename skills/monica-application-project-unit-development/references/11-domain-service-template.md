# DomainService Template

Use `$DomainNamespace$` for the domain project namespace selected by the architecture skill, such as `Domains.Ordering`.

## Use When

- A meaningful business rule has no natural entity owner, often because it spans aggregates.
- Multiple entry points need that rule. Reuse can justify a service, but does not move an entity's invariant out of it.

A handler that loads one order and calls `order.Approve()` does not need a DomainService. Keep use-case orchestration in the handler; do not add a forwarding service for structural symmetry.

## Rules

- Derive from `DomainService`.
- Declare business collaborators in the constructor, preferably a primary constructor, and inject only dependencies the service uses. Current-user access and ID generation are explicit application dependencies; do not add them to a shared service base or resolve them through `IServiceProvider` / `CachedServiceProvider`.
- Use the inherited `Logger` in service methods when logging is needed. Monica resolves it after activation from the
  host that owns the service instance; do not access it from a constructor.
- Start the class name with `Domain`.
- Prefer normal return types and exceptions. Let `ApplicationService` translate failures into `Res`.
- Keep persistence access delegated to repositories.
- Keep state mutation on entities whenever the entity is the natural owner of the rule.

## Minimal Example

`Order`, `Warehouse`, and their repositories are application-owned example types. Here fulfillment is a cross-aggregate rule; the order still protects its own approval transition.

```csharp
using Monica.ProjectUnits.Annotations;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;

namespace $DomainNamespace$.DomainServices;

[ProjectUnitMetadata(
    "$FeatureName$ Domain Rules",
    Description = "Enforces reusable domain rules for $FeatureName$.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed class Domain$FeatureName$(
    IRepositoryOrder orders,
    IRepositoryWarehouse warehouses) : DomainService
{
    public async Task<Order> ExecuteAsync(
        long orderId,
        long warehouseId,
        CancellationToken cancellationToken = default)
    {
        var order = await orders.GetAsync(orderId, cancellationToken);
        var warehouse = await warehouses.GetAsync(warehouseId, cancellationToken);
        if (!warehouse.CanFulfill(order))
        {
            throw new InvalidOperationException("Warehouse cannot fulfill this order.");
        }

        order.Approve();
        return order;
    }
}
```

## Heuristics

- If the method only loads an aggregate and invokes one entity method, the handler can orchestrate it directly.
- If the method only wraps a materialized query, the repository may be the better home.
- If the method mostly toggles fields on one entity, move that behavior onto the entity.
- If the method starts coordinating transport or UI concerns, it is no longer a `DomainService`.
