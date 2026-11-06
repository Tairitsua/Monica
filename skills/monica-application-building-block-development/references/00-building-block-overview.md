# Building Block Overview

A **Monica Building Block** is an application code element with a defined responsibility that Monica recognizes and describes through a supported kind contract. A concrete class or record normally represents one building block. Requests, events and options are building blocks even when they do not execute work. Use **构件** in Chinese and retain `BuildingBlock` in identifiers.

Use this file to choose the owner of behavior within the project's adopted application specification. The kind table lists supported choices; a feature needs only the building blocks that fulfill its actual behavior. Use the [composition map](02-building-block-composition-map.md) for feature selection.

**Kind** is the building block classification, such as Entity or Repository. **Role** is a domain-owned application responsibility and placement boundary, such as `ordering.reporting`; it is not a synonym for kind. Framework modules compose infrastructure. `Platform.Foundation` contains independent reusable code and is not the building block catalog. A dependency graph reports static relationships, not proof of a runtime execution sequence.

Runtime and source discovery recognize the kinds below from inheritance, interfaces and explicit attributes, with one primary kind per type. Naming conventions report separate diagnostics. Metadata describes a discovered type; it does not make an arbitrary helper eligible, and no common application `IBuildingBlock` marker or base is required. A discovered type can still violate application rules or lack approval. Reserved enum values outside `BuildingBlockSourceAnalysisContract.DiscoverableKinds` are not implemented kinds.

| Kind | Base type or contract | Naming convention | Primary responsibility |
| --- | --- | --- | --- |
| `ApplicationRequestHandler` | `ApplicationRequestHandler<TRequest, TResponse>`; `TResponse` is the complete mediator response | `CommandHandler*`, `QueryHandler*`, or another `*Handler*` name | Transport-independent mediator use-case orchestration (HTTP exposure is opt-in) with a deliberate result boundary |
| `RequestDto` | `IQuery<TData>` or `ICommand<TData>`/`ICommand` | `Command*`, `Query*`, `Request*` | Stable request contract for a use case |
| `DomainService` | `DomainService` | `Domain*` | Meaningful domain rule with no natural entity owner, often spanning aggregates |
| `Entity` | `Entity<TKey>` or `Entity` plus `IEntity` | Place under `Entities/` | Own state, invariants, and behavior |
| `Repository` | Minimal `IRepository<TEntity>` or keyed contract plus concrete implementation | `IRepository{Aggregate}` / exact `Repository{Aggregate}` | Tracked aggregate loading, staging, and materialized business reads |
| `DomainEvent` | `DomainEvent` | `Event*` | An application fact or shared notification contract worth publishing or reacting to |
| `DistributedEventHandler` | `DistributedEventHandler<TEvent>` | `DistributedEventHandler*` | React to distributed events |
| `LocalEventHandler` | `LocalEventHandler<TEvent>` | `LocalEventHandler*` | React to in-process events |
| `Configuration` | `[Configuration]` class | `Options*` | Typed deployment-owned Startup or runtime Managed settings |
| `RecurringJob` | `RecurringJob` | `Worker*` | Scheduled background work |
| `TriggeredJob` | `TriggeredJob<TArgs>` | `Job*` | On-demand asynchronous work |
| `CrudApplicationService` | `ApplicationService` plus `ICrudApplicationService`, normally native `CrudApplicationService<...>` | Project convention; configured CRUD suffix is a route aid | Bounded persistence-facing CRUD adapter |
| `Seeder` | `ISeeder`, normally `SeederBase` | Project convention; no native prefix rule | Finite, idempotent startup preparation |
| `HostedService` | `IHostedService`; Monica observation uses `MoHostedService` / `MoBackgroundService` | `{Purpose}HostedService` / `{Purpose}BackgroundService`; no native naming diagnostic | Host lifecycle or continuous infrastructure work |
| `HttpApi` | `ControllerBase` | Usually `*Controller`; no native prefix rule | HTTP transport adapter, including generated mediated controllers |

For concrete domain-core and role-project folder placement, use [01-building-block-naming-and-boundaries.md](01-building-block-naming-and-boundaries.md).

The framework also recognizes `IDomainEvent`, `IDistributedEventHandler<TEvent>`, and `ILocalEventHandler<TEvent>` implementations. The table selects the base classes used by application building blocks; transport-level interface support does not replace those conventions. Read [adapters and lifecycle types](04-adapters-lifecycle-and-support-types.md) before selecting CRUD, seeding, host, or controller kinds.

Ordinary internal `IRequestHandler<TRequest, TResult>` handlers, Facades, DTOs/value objects, modules, mappers, validators, and pure helpers are support types unless their actual shape matches a discovered kind. `IResultRequest` itself is discoverable as `RequestDto` even when its handler is internal. Do not add a fake building block base or new application role merely to make a support type appear in the catalog.

## Source Analysis

Consumers of `IBuildingBlockSourceAnalyzer` should supply `BuildingBlockSourceAnalysisRequest.SolutionPath` when the selected workspace has a solution. The analyzer uses a temporary solution filter to batch the requested production and test projects without adding unselected projects to the catalog. Referenced projects still provide semantic context. Selected projects absent from the solution, or workspaces without a solution, use individual loading. All selected projects compile against the final loaded solution so shared references and source generators are not repeatedly compiled from earlier snapshots.

The original solution supplies `SolutionDir` for input evaluation, loading, and recovery. The solution file participates in disposable input freshness observations. Progress reports input evaluation separately from loading; both counts include production and test projects. These source observations do not execute the application or its tests, and they do not establish test coverage or authorize changes.

## Agent Context Contract

Every application-authored discovered building block must declare its own human-readable responsibility and associate applicable actual requirement IDs when they can be resolved. Generated types remain generator-owned. Missing legitimate traceability is [coverage debt](03-building-block-context-metadata.md), not permission to invent a behavioral commitment:

```csharp
using Monica.BuildingBlocks.Annotations;

[BuildingBlockMetadata(
    "Approve Order",
    Description = "Approves an eligible order.",
    Tags = ["ordering", "approval"])]
[BuildingBlockRequirement("ORD-REQ-001")]
public sealed class CommandHandlerApproveOrder : ApplicationRequestHandler<CommandApproveOrder, Result>
{
    // Implementation omitted.
}
```

The metadata annotation is explicit and non-inherited. Add it to each request, handler, domain service, entity, repository implementation, event, event handler, job, and configuration that Building Blocks discovers. Requirement annotations are repeatable; Monica trims and deduplicates IDs case-insensitively without imposing a project-specific format.

The status dashboard measures four independent dimensions across every discovered building block:

- **Metadata:** an explicit `BuildingBlockMetadataAttribute` exists.
- **Description:** metadata supplies a description or XML documentation supplies a summary.
- **Ownership:** metadata supplies a non-empty owner.
- **Requirements:** at least one `BuildingBlockRequirementAttribute` exists.

An empty catalog reports no data, not 100% coverage. Use [03-building-block-context-metadata.md](03-building-block-context-metadata.md) before filling a building block template.

## Selection Rules

- Declare `[Configuration]` directly on every configuration Building Block; configuration identity is not inherited by derived options types.
- Apply `[ExcludeFromBusinessTypeDiscovery]` directly to a runtime-only type that must be absent from both runtime and source Building Block catalogs. The exclusion does not flow to derived types.
- Use `BuildingBlockSourceAnalysisContract.DiscoverableKinds` when validating a source-level create operation's kind; not every reserved `SourceBuildingBlockKind` value is implemented by the current analyzer.

The `monica-building-blocks-source/v1` analysis contract reports public instance properties (including inherited members), source nullability, required initialization, setter access, annotations, XML summaries and public/protected behavior signatures. Each available source location is retained. These are declared CLR facts, not inferred persistence mappings. For physical mappings use an explicitly selected EF design model through the persistence skill's [entity model inspection](../../monica-infra-persistence/references/repository-diagnostics.md#inspect-an-explicitly-selected-entity-design-model). Keep business meanings and null semantics near their chosen source or owned design; avoid copying type/schema inventories into every delivery plan.
- Create a new `ApplicationRequestHandler` when you need a new externally visible use case.
- Create a `DomainService` only for a meaningful domain rule without a natural entity owner, including a rule reused across entry points or spanning aggregates. A one-entity transition normally stays on the entity and the handler calls it directly.
- Create or extend an `Entity` when the rule belongs to the state owner itself.
- Create a `Repository` only when persistence access is needed. Do not hide pure business rules inside repositories.
- Create a `DomainEvent` only when the fact itself matters to other handlers or modules. Do not emit events for trivial internal control flow.
- Create a `Configuration` class only for genuine host-configurable behavior, not for constants.
- Choose events, jobs, outbox/inbox, or retry policies for a demonstrated delivery or execution requirement. An ordinary command does not acquire a reliability architecture merely because those capabilities exist.

## Placement

This skill does not decide the outer folder layout.

- Use the [unified architecture](../../monica-application/references/03-unified-architecture.md) for domain cores, role libraries, Protocol, selective Host composition and top-level migration placement.
- Use the microservice/modular-monolith skills only for split/combined deployment and migration from existing layouts.
