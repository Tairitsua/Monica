# Entity, Request, and Event Template

Use `$ContractNamespace$` for the contract project selected by the architecture skill, such as `Platform.Protocol.PublishedLanguages.DomainOrdering`. Use `$DomainNamespace$` for the domain project namespace selected by the architecture skill, such as `Domains.Ordering`.

## Request Contract

Use request contracts for use-case input. Keep them stable and serializable.

```csharp
using Monica.ProjectUnits.Annotations;
using Monica.Application.Abstractions;
using Monica.Application.Annotations;

namespace $ContractNamespace$.Requests;

/// <summary>Reads the $FeatureName$ view.</summary>
[ApiEndpoint(ApiHttpMethod.Get, "queries/$FeatureName$/{Id}")]
[ProjectUnitMetadata(
    "$FeatureName$ Query",
    Description = "Requests the $FeatureName$ query use case.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed record Query$FeatureName$(long Id) : IQuery<$ResponseName$>;

/// <summary>Executes the $FeatureName$ action.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "commands/$FeatureName$")]
[ProjectUnitMetadata(
    "$FeatureName$ Command",
    Description = "Requests the $FeatureName$ command use case.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed record Command$FeatureName$(long Id, string Reason) : ICommand;
```

Rules:

- Use request types for commands and queries, not for persistence state.
- Keep request contracts small and explicit.
- Place response DTOs near contracts, not near persistence.
- One ApiEndpoint(method, route) declares the operation together with HTTP method, relative route and optional Binding. Generated method names always remove the request name's Command or Query prefix. Local calls retain this metadata without using HTTP. Handlers never repeat MVC metadata.
- Requests in the exact PublishedLanguages.Domain{DomainName}.Requests namespace generate domain interfaces without HTTP configuration. Hosts select adapters and owned receivers; requests elsewhere do not enter published API contracts.
- Published API result envelopes must implement `IRemoteResultEnvelope<TSelf>`. Built-in `Res`, `Res<T>`, and `ResPaged<T>` already do; custom envelopes must construct remote failures through `CreateRemoteFailure` without bypassing their invariants.
- Treat moving a request into or out of `PublishedLanguages` as a public compatibility decision because it changes API client generation.
- Use `DateTime` only for a timezone-free wall-clock value. Monica's default wire policy writes it as `yyyy-MM-dd'T'HH:mm:ss.FFFFFFF`; a host may select the built-in space-separated wall-clock policy for its complete API contract. Both policies preserve significant fractional ticks and omit `Kind`, and receivers treat the value as `DateTimeKind.Unspecified`.
- Use `DateTimeOffset` for an instant or explicit offset; its round-trip `"O"` representation preserves both. Do not use `DateTime` plus an assumed machine-local zone for instant semantics.
- Rely on generated query/route serialization and the owning host's JSON options for body requests. Do not hand-format temporal fields or add endpoint-specific `SpecifyKind`, UTC, or local-time compensation.

## Entity

Use entities to own identity, state transitions, and invariants.

```csharp
using Monica.ProjectUnits.Annotations;
using Monica.Repository.Entity.Abstractions;

namespace $DomainNamespace$.Entities;

[ProjectUnitMetadata(
    "Order",
    Description = "Owns order state transitions and invariants.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed class Order : Entity<long>
{
    public string Number { get; private set; } = string.Empty;
    public bool IsApproved { get; private set; }

    public Order(long id, string number)
    {
        Id = id;
        Rename(number);
    }

    public void Rename(string number)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new ArgumentException("Order number is required.", nameof(number));
        }

        Number = number.Trim();
    }

    public void Approve()
    {
        if (IsApproved)
        {
            throw new InvalidOperationException("Order is already approved.");
        }

        IsApproved = true;
    }

    public override void AutoSetNewId(bool notSetWhenNotDefault = false)
    {
        if (notSetWhenNotDefault && Id != default)
        {
            return;
        }

        Id = 0;
    }
}
```

Rules:

- Keep behavior close to the state it protects.
- Avoid public setters unless a property is intentionally unconstrained.
- Keep EF mapping concerns in persistence configuration or `DbContext`, not in the request contract.

## Domain Event

Use domain events to describe meaningful business facts or shared application notifications. Follow the authoritative [event naming and placement rules](01-project-unit-naming-and-boundaries.md), including for technical envelopes.

```csharp
using Monica.EventBus.Annotations;
using Monica.EventBus.Events;
using Monica.ProjectUnits.Annotations;

namespace $ContractNamespace$.Events;

public static class Const
{
    public const string EventOrderApproved = "ordering.order-approved";
}

[EventName(Const.EventOrderApproved)]
[ProjectUnitMetadata(
    "Order Approved Event",
    Description = "Publishes the fact that an order was approved.",
    Tags = ["$SubdomainTag$", "$FeatureTag$"])]
[ProjectUnitRequirement("$RequirementId$")]
public sealed class EventOrderApproved : DomainEvent
{
    public long OrderId { get; init; }
    public string Number { get; init; } = string.Empty;
}
```

Rules:

- Name events after facts, not handlers.
- Define the transport identity once in the owning published language's constant container (`Const` in this template) and reference it directly from `[EventName]`. Reuse an existing constant entry and container when available; do not add a duplicate alias on the event class.
- Keep event payloads stable, serializable, and focused on what consumers need.
- Do not expose the full entity graph in an event.
- Apply the same temporal meaning to events: `DateTime` is a timezone-free wall-clock value, while `DateTimeOffset` represents an instant or explicit offset.
- Add delivery metadata such as `[Outbox]` using the persistence skill's [transactional-event guidance](../../monica-infra-persistence/references/transactional-events.md); delivery configuration does not replace the event's base class, metadata, or placement.
- When renaming or relocating an existing contract, inspect its declared event name and every explicit topic override. Preserve deployed topic identities deliberately rather than allowing a CLR-name change to rename the transport contract.
