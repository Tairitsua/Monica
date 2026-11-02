# Event Handler Template

Use `$ApplicationNamespace$`, `$DomainNamespace$`, and `$ContractNamespace$` for the application, domain, and published-language namespaces selected by the architecture skill.

## Use When

- Another unit should react to an event without direct coupling to the sender.
- The reaction should complete as part of processing that event delivery.

## Rules

- Use `DomainEventHandler<TEvent>` for distributed events.
- Use `DomainBatchEventHandler<TEvent>` only for an explicit consumer that combines database work. Keep the `DomainEventHandler` naming prefix and ProjectUnit metadata; implement `HandleBatchAsync(IReadOnlyList<TEvent>, CancellationToken)` and select its transaction context on that method. Follow [batch receive prerequisites and limits](../../monica-infra-messaging/references/event-bus.md#explicit-batch-consumers) and [batch Inbox](../../monica-infra-persistence/references/transactional-events.md#inbox-and-timestamp-behavior). Keep ordinary handlers for independent reactions that gain nothing from combining work.
- Use `LocalEventHandler<TEvent>` for in-process reactions.
- Use the inherited `Logger` in handler methods when logging is needed. Monica resolves it after activation from the
  host that owns the handler instance; do not access it from a constructor.
- Keep handlers focused on the reaction. Entities own invariants; delegate to a DomainService only when a meaningful domain rule warrants one.
- Make the event type stable before adding consumers.
- Use `$ApplicationNamespace$` for the application-layer namespace chosen by the architecture skill.
- Place handlers in `HandlersEvent/`.
- Use the [naming and boundary rules](01-project-unit-naming-and-boundaries.md) for every application consumer, including consumers marked `[Inbox]`; delivery attributes do not replace its ProjectUnit base, prefix, or metadata.
- Choose the execution boundary using [transactional events](../../monica-infra-persistence/references/transactional-events.md). Repository `IDomainEventHandler<TEvent>` handles before-commit effects in the publisher's scope; it is a different contract from these EventBus handler bases.
- Put external subscription entry points in their actual owning role and keep reusable domain reactions passive. Manual mode holds distributed consumption while preserving ordinary local reactions; use the native [targeted test-event operation](../../monica-infra-messaging/references/event-bus.md#manual-delivery-and-held-subscriptions) rather than activating a broker subscription for a local test.

## Distributed Handler Example

```csharp
using Monica.ProjectUnits.Annotations;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using $ContractNamespace$.Events;
using $DomainNamespace$.DomainServices;

namespace $ApplicationNamespace$.HandlersEvent;

[ProjectUnitMetadata(
    "Notify Warehouse After Order Approval",
    Description = "Coordinates the warehouse reaction to an approved order.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed class DomainEventHandlerOrderApproved(DomainNotifyWarehouse domainService)
    : DomainEventHandler<EventOrderApproved>
{
    public override async Task HandleEventAsync(
        EventOrderApproved eventData,
        CancellationToken cancellationToken)
    {
        await domainService.ExecuteAsync(eventData.OrderId, cancellationToken);
    }
}
```

## Batch Handler Example

```csharp
using Monica.Core.Execution;
using Monica.ProjectUnits.Annotations;
using Monica.Repository.Inbox.Annotations;
using Monica.WebApi.Abstractions;

[ProjectUnitMetadata("Record Order Notices", Description = "Stores new notices and their Inbox receipts in one transaction.")]
[Inbox("orders.record-notices.v1")]
public sealed class DomainEventHandlerRecordNotices(IRepositoryNotice repository)
    : DomainBatchEventHandler<EventOrderNotice>
{
    [ExecutionTransaction(ExecutionTransactionMode.Automatic, DbContextTypes = new[] { typeof(OrdersDbContext) })]
    public override Task HandleBatchAsync(IReadOnlyList<EventOrderNotice> events, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        repository.AddRange(events.Select(item => new Notice(item.OrderId, item.Text)));
        return Task.CompletedTask;
    }
}
```

Register `AddInbox<OrdersDbContext>()` and its schema before receiving. The operation boundary owns saving and committing the group; do not commit per item. `AddRange` retains entity projections. Native bulk writes require the separate persistence contract and do not automatically generate entity events.

## Local Handler Example

```csharp
using Monica.ProjectUnits.Annotations;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using $ContractNamespace$.Events;
using $DomainNamespace$.DomainServices;

namespace $ApplicationNamespace$.HandlersEvent;

[ProjectUnitMetadata(
    "Refresh Order Read Model",
    Description = "Refreshes the local read model after order approval.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed class LocalEventHandlerOrderApproved(DomainRefreshReadModel domainService)
    : LocalEventHandler<EventOrderApproved>
{
    public override async Task HandleEventAsync(
        EventOrderApproved eventData,
        CancellationToken cancellationToken)
    {
        await domainService.ExecuteAsync(eventData.OrderId, cancellationToken);
    }
}
```

## Notes

- Keep work in the handler when it belongs to processing that delivery. Choose a [TriggeredJob](15-job-template.md) when work needs an independent execution lifecycle, such as delayed scheduling, operator control, concurrency policy, or execution history. Use [transactional events](../../monica-infra-persistence/references/transactional-events.md) for durable delivery and deduplicated receives.
- Pass the handler `CancellationToken` into every cancellable dependency. A timeout consumes the processing retry
  budget, but it cannot forcibly terminate handler code that ignores cancellation.
- Follow the [EventBus failure contract](../../monica-infra-messaging/references/event-bus.md#processing-retries-and-dead-letters) for the shared finite retry budget and dead-letter handling; persist expected alarm or rejection outcomes before returning normally.
- Do not let handlers become alternate application services with large control flow and validation logic.
