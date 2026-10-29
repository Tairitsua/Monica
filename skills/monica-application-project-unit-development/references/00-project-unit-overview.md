# ProjectUnit Overview

Use this file to choose the owner of behavior within the project's adopted application specification. The role table defines native unit choices; it does not require every feature to create every role. Use the [composition map](02-project-unit-composition-map.md) for feature selection.

The current runtime and source discovery implementations recognize the following roles. Classification follows inheritance/interfaces and explicit attributes; naming conventions report separate diagnostics. A discovered type can still violate application rules or lack approval. Reserved enum values outside `ProjectUnitSourceAnalysisContract.DiscoverableUnitTypes` are not implemented source roles.

| Unit | Base type or contract | Naming convention | Primary responsibility |
| --- | --- | --- | --- |
| `ApplicationService` | `ApplicationService<TRequest, TResponse>`, `ApplicationService<TRequest>`, or native `CustomApplicationService<TRequest, TResult>` | `CommandHandler*`, `QueryHandler*`, or another `*Handler*` name | Transport-independent mediator use-case orchestration (HTTP exposure is opt-in) with a deliberate result boundary |
| `RequestDto` | `IQuery<TResponse>` or `ICommand<TResponse>`/`ICommand` | `Command*`, `Query*`, `Request*` | Stable request contract for a use case |
| `DomainService` | `DomainService` | `Domain*` | Meaningful domain rule with no natural entity owner, often spanning aggregates |
| `Entity` | `Entity<TKey>` or `Entity` plus `IEntity` | Place under `Entities/` | Own state, invariants, and behavior |
| `Repository` | Minimal `IRepository<TEntity>` or keyed contract plus concrete implementation | `IRepository{Aggregate}` / exact `Repository{Aggregate}` | Tracked aggregate loading, staging, and materialized business reads |
| `DomainEvent` | `DomainEvent` | `Event*` | An application fact or shared notification contract worth publishing or reacting to |
| `DomainEventHandler` | `DomainEventHandler<TEvent>` | `DomainEventHandler*` | React to distributed events |
| `LocalEventHandler` | `LocalEventHandler<TEvent>` | `LocalEventHandler*` | React to in-process events |
| `Configuration` | `[Configuration]` class | `Options*` | Typed deployment-owned Startup or runtime Managed settings |
| `RecurringJob` | `RecurringJob` | `Worker*` | Scheduled background work |
| `TriggeredJob` | `TriggeredJob<TArgs>` | `Job*` | On-demand asynchronous work |
| `CrudApplicationService` | `ApplicationService` plus `ICrudApplicationService`, normally native `CrudApplicationService<...>` | Project convention; configured CRUD suffix is a route aid | Bounded persistence-facing CRUD adapter |
| `Seeder` | `ISeeder`, normally `SeederBase` | Project convention; no native prefix rule | Finite, idempotent startup preparation |
| `HostedService` | `IHostedService`; Monica observation uses `MoHostedService` / `MoBackgroundService` | `{Purpose}HostedService` / `{Purpose}BackgroundService`; no native naming diagnostic | Host lifecycle or continuous infrastructure work |
| `HttpApi` | `ControllerBase` | Usually `*Controller`; no native prefix rule | HTTP transport adapter, including generated mediated controllers |

For concrete domain-core and role-project folder placement, use [01-project-unit-naming-and-boundaries.md](01-project-unit-naming-and-boundaries.md).

The framework also recognizes `IDomainEvent`, `IDistributedEventHandler<TEvent>`, and `ILocalEventHandler<TEvent>` implementations. The table selects the base classes used by application units; transport-level interface support does not replace those conventions. Read [adapters and lifecycle types](04-adapters-lifecycle-and-support-types.md) before selecting CRUD, seeding, host, or controller roles.

Ordinary internal `IRequestHandler<TRequest, TResult>` handlers, Facades, DTOs/value objects, modules, mappers, validators, and pure helpers are support types unless their actual shape matches a discovered role. `IResultRequest` itself is discoverable as `RequestDto` even when its handler is internal. Do not add a fake ProjectUnit base or new application role merely to make a support type appear in the catalog.

## Source Analysis

Consumers of `IProjectUnitSourceAnalyzer` should supply `ProjectUnitSourceAnalysisRequest.SolutionPath` when the selected workspace has a solution. The analyzer uses a temporary solution filter to batch the requested production and test projects without adding unselected projects to the catalog. Referenced projects still provide semantic context. Selected projects absent from the solution, or workspaces without a solution, use individual loading. All selected projects compile against the final loaded solution so shared references and source generators are not repeatedly compiled from earlier snapshots.

The original solution supplies `SolutionDir` for input evaluation, loading, and recovery. The solution file participates in disposable input freshness observations. Progress reports input evaluation separately from loading; both counts include production and test projects. These source observations do not execute the application or its tests, and they do not establish test coverage or authorize changes.

## Agent Context Contract

Every application-authored discovered unit must declare its own human-readable responsibility and associate applicable actual requirement IDs when they can be resolved. Generated types remain generator-owned. Missing legitimate traceability is [coverage debt](03-project-unit-context-metadata.md), not permission to invent a behavioral commitment:

```csharp
using Monica.ProjectUnits.Annotations;

[ProjectUnitMetadata(
    "Approve Order",
    Description = "Approves an eligible order.",
    Tags = ["ordering", "approval"])]
[ProjectUnitRequirement("ORD-REQ-001")]
public sealed class CommandHandlerApproveOrder : ApplicationService<CommandApproveOrder>
{
    // Implementation omitted.
}
```

The metadata annotation is explicit and non-inherited. Add it to each request, handler, domain service, entity, repository implementation, event, event handler, job, and configuration that ProjectUnits discovers. Requirement annotations are repeatable; Monica trims and deduplicates IDs case-insensitively without imposing a project-specific format.

The status dashboard measures four independent dimensions across every discovered unit:

- **Metadata:** an explicit `ProjectUnitMetadataAttribute` exists.
- **Description:** metadata supplies a description or XML documentation supplies a summary.
- **Ownership:** metadata supplies a non-empty owner.
- **Requirements:** at least one `ProjectUnitRequirementAttribute` exists.

An empty catalog reports no data, not 100% coverage. Use [03-project-unit-context-metadata.md](03-project-unit-context-metadata.md) before filling a unit template.

## Selection Rules

- Declare `[Configuration]` directly on every configuration ProjectUnit; configuration identity is not inherited by derived options types.
- Apply `[ExcludeFromBusinessTypeDiscovery]` directly to a runtime-only type that must be absent from both runtime and source ProjectUnit catalogs. The exclusion does not flow to derived types.
- Use `ProjectUnitSourceAnalysisContract.DiscoverableUnitTypes` when validating a source-level create role; not every reserved `ProjectUnitSourceType` value is implemented by the current analyzer.
- Create a new `ApplicationService` when you need a new externally visible use case.
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
