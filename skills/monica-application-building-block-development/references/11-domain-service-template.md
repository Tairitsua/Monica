# DomainService Template

Use `$DomainNamespace$` for the domain project namespace selected by the architecture skill, such as `Domains.Ordering`.

## Use When

Use a `DomainService` for a rule with no single entity owner, or for a named business operation that coordinates entities, repositories, and other domain services within one bounded context. A single command, event handler, or job can be its only caller. The service may perform the whole business sequence, choose the order of its steps, apply business rejection rules, and return the outcome to the caller. Each entity still protects its own invariants.

The entry point and execution pipeline own dispatch, scope, transaction participation, and retry policy. When a retry requires fresh tracked state, obtain the domain service inside the new attempt's scope and use that instance for the business sequence. The handler can then map its outcome to the public response. A simple handler that loads an order and calls `order.Approve()` needs no additional service.

## Rules

- Derive from `DomainService` in `Monica.Application.Abstractions`. The base carries the transient DI convention.
- Declare business collaborators in the constructor, preferably a primary constructor, and inject only dependencies the service uses. Current-user access and ID generation are explicit application dependencies; do not add them to a shared service base or resolve them through `IServiceProvider` / `CachedServiceProvider`.
- Use the inherited `Logger` in service methods when logging is needed. Monica resolves it after activation from the
  host that owns the service instance; do not access it from a constructor.
- Start the class name with `Domain`.
- Prefer normal return types and exceptions. Let `ApplicationRequestHandler` translate failures into `Result`.
- Keep persistence access delegated to repositories.
- Keep state mutation on entities whenever the entity is the natural owner of the rule.

## Minimal Example

`Order`, `Warehouse`, and their repositories are application-owned example types. One approval command checks warehouse capacity, reserves stock, and approves the order in the caller's transaction. The warehouse rejects a reservation when stock is insufficient. The order owns its approval transition. The handler can return the approved order's identifier after the operation succeeds.

```csharp
using Monica.ApplicationModel.Annotations;
using Monica.Application.Abstractions;

namespace $DomainNamespace$.DomainServices;

[BuildingBlockMetadata(
    "$FeatureName$ Domain Rules",
    Description = "Coordinates stock reservation and order approval.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[BuildingBlockRequirement("$RequirementId$")]
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
        warehouse.ReserveStock(order);
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
