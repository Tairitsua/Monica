# Building Block Naming, Placement, and Boundaries

Use these rules to keep Monica building-block discovery, folder layout, code navigation, and DDD boundaries aligned.

## Default Naming Rules

These conventions mirror the current `Monica.BuildingBlocks` naming diagnostics. Classification uses type contracts; a passing name is not proof of architectural compliance. Repository pairing is an additional eligibility constraint: the implementation must implement the interface whose simple name is exactly `I` plus its class name.

| Kind | Default naming convention | Recommended naming |
| --- | --- | --- |
| `ApplicationRequestHandler` | Class name contains `Handler` | Prefer `CommandHandler*` or `QueryHandler*` |
| `DomainService` | Class name starts with `Domain` | `Domain*` |
| `Repository` implementation | Class name starts with `Repository`; exact `I{ClassName}` interface is required for classification | `Repository{Aggregate}` and `IRepository{Aggregate}` |
| `DomainEvent` | Class name starts with `Event` | `Event*` |
| `DistributedEventHandler` | Class name starts with `DistributedEventHandler` | `DistributedEventHandler*` |
| `LocalEventHandler` | Class name starts with `LocalEventHandler` | `LocalEventHandler*` |
| `Entity` | Namespace contains `Entities` | Place under `Entities/` |
| `Configuration` | Class name starts with `Options` | `Options{Purpose}` |
| `RecurringJob` | Class name starts with `Worker` | `Worker*` |
| `TriggeredJob` | Class name starts with `Job` | `Job*` |
| `HostedService` | No native naming diagnostic | `{Purpose}HostedService` for resource lifecycle; `{Purpose}BackgroundService` for a continuous loop |
| `RequestDto` | Implements `IQuery<T>` or `ICommand<T>`/`ICommand`; these retain `IResultRequestBase` discovery | Prefer `Command*`, `Query*`, or `Request*` |

Naming checks are disabled until the host enables `Naming.Enabled`. A rule inherits the global `Mode` unless it explicitly overrides that mode; Strict rejects violations and Warning retains a diagnostic. The Options prefix applies to application-authored `[Configuration]` Building Blocks of both Startup and Managed kinds, not Monica/Microsoft option types. Preserve issued section paths and definition keys when renaming a Managed type; see the [configuration template](16-configuration-template.md).

## Source File Names

- Name a standalone Building Block source file exactly after its primary CLR type, followed by `.cs`. Retain the complete prefix: `CommandApproveOrder` belongs in `CommandApproveOrder.cs`, and `CommandHandlerApproveOrder` belongs in `CommandHandlerApproveOrder.cs`.
- Give independent command and query contracts separate files named after their respective types. Do not drop `Command`, `Query`, or `Request` from published request filenames.
- Local HTTP-only request and response types may share their handler's file; name that file after the handler. A closely coupled response DTO may share a published request's file; name that file after the primary request type.
- When adding a building block, follow this rule even if nearby legacy files omit their type prefixes.

## Additional Support-Type Conventions

These names describe support types; they do not create additional discovered kinds. Adopt concrete placement in the project specification and use [adapter/support rules](04-adapters-lifecycle-and-support-types.md) where relevant.

| Support type | Recommended naming | Purpose |
| --- | --- | --- |
| `Utility` helper | `Utils*` | Pure helper code that stays inside the domain boundary and does not deserve its own `DomainService` |
| Facade | `*Facade` | Existing deliberate adapter; new business UI normally consumes application use cases |
| Internal mediator handler | `CommandHandler*`, `QueryHandler*`, or meaningful `*Handler*` | Internal use-case orchestration through the existing mediator contract |
| Response/read DTO, value object, mapper, validator | Business meaning plus an existing repository convention | Data shape, invariant value, mapping, or validation without a new building block kind |
| Composition module | Existing `Module*` convention | Registration and host integration, for product bootstrap and explicit integrations; not one module per business feature |
| Hosted-service work item and state | `{Purpose}WorkItem`, `{Purpose}State` or `{Purpose}Buffer` | Finite scoped processing and explicitly owned process state beside `HostedServices/`; not new Building Block families |

## Project-Level Extensions

Project-level extensions specialize an existing building block kind. A specialization does not implicitly authorize another building block kind, application role, architecture layer, or transaction/job framework. Use the native Monica type directly when it already supplies the required behavior and contract. The project's `<project>-application-spec` owns its adopted families, consumers, rationale, and differences. Follow [specification evolution](../../monica-application/references/01-specification-evolution.md) before adopting a new shared base family or a deviation; a new concrete type in an already approved family is ordinary implementation.

Distinguish three naming decisions:

| Type | Naming rule | Example |
| --- | --- | --- |
| Native capability | Keep its Monica name | `ApplicationRequestHandler`, `DomainService`, `RecurringJob` |
| Reusable product specialization | Product identity plus a meaningful capability or kind | `{Product}Entity`, `{Product}EfRepository`, `I{Product}CurrentUser` |
| Concrete business building block or support type | Keep its business meaning and the existing kind convention | `Order`, `IRepositoryOrder`, `RepositoryOrder`, `CommandHandlerApproveOrder` |

Choose the product identity in the project's application specification. `Our`, `My`, `Custom`, and `Ex` do not establish ownership or responsibility. Use `Extensions` only for actual extension methods, such as `{Product}EntityIdExtensions`; it is not a general container for bases, DTOs, persistence, and startup logic.

### Choose the Existing Mechanism

1. Use native configuration when it expresses the required behavior.
2. Implement an existing extension point for a policy or behavior, such as `IPersistencePolicy`; preserve its execution and ownership contract.
3. Inherit when a stable type specialization supplies a useful common contract, consumed marker, state structure, or framework lifecycle adaptation.
4. Compose independently varying capabilities through explicit dependencies. Keep an existing cohesive implementation when splitting it adds no meaningful boundary.

A thin or empty class body can be justified by a marker that has real consumers or by a stable contract/state shape. A naming alias, speculative future use, or fewer constructor parameters is not sufficient value. Document the protected behavior, inputs and outputs, dependencies, lifetime/ownership, allowed consumers, and the source or behavioral evidence for that value.

Inject the dependencies a concrete building block actually uses. Do not create a common service toolbox exposing current user, ID generation, events, clocks, and unrelated capabilities to every subclass. Do not hide business dependencies behind `IServiceProvider`, a cached service locator, a new scanner, or an independently built service provider; use the established host registration and lifecycle.

### Keep Ownership and Consumers Explicit

| Responsibility | Existing owner |
| --- | --- |
| Product-specific technical code on Monica reused by multiple domain/role owners | `Platform.Infrastructure`, in a folder named for the capability |
| A standalone product-independent and Monica-independent helper library | Optional `Platform.Foundation` |
| Shared business language and published contracts | `Platform.Protocol/PublishedLanguages` |
| Whole-host modules/providers, UI shell, middleware and endpoints | The executable's `Composition/` folder |
| Passive and active registration of one domain or role | Its assembly-owned `ApplicationRegistration` |
| Shared test factories and doubles, without test cases | Optional `Platform.Testing` |
| A domain's repository, adapter, or other implementation | The owning domain core or role library |

Preserve the [shared dependency direction](../../monica-application/references/03-unified-architecture.md#physical-ownership). `Platform.Foundation` references no Monica or other Platform project. Infrastructure may consume Protocol but not domain cores, roles or hosts. Protocol holds published shapes and identifiers, without options, provider/UI composition or copied environment files. A single-consumer implementation stays with its owner, and broadly reusable Monica mechanisms belong upstream. Capability folders for shared extensions do not change the domain's `Repository/` placement.

An EF specialization such as `{Product}EfRepository` and its wide adapter contract describe infrastructure access. Handlers, jobs, and domain services consume the narrow domain repository interface. Only a persistence-facing CRUD adapter in an approved/adopted adapter pattern may consume the concrete implementation or EF contract under the [repository template](13-repository-and-persistence-template.md). A new concrete endpoint in that family is ordinary implementation; the exception does not authorize a new family or general concrete injection. The same implementation serves both views; do not add a forwarding repository layer.

Shared bases, markers, policies, and other support types do not automatically become business Building Blocks. Determine discovery from the actual type and framework rules. Each discovered concrete building block retains its own explicit metadata and actual requirement references under the [metadata contract](03-building-block-context-metadata.md); a base cannot supply these and examples must not invent requirement IDs.

Validate a specialization through its owning behavior: host registration/discovery, scoped repository/context identity, tracked mutation, operation commit and rollback, or the adapter's existing semantics. Use `$monica-application-unit-testing` when executable verification is needed. Reconcile adoption and differences in the project specification instead of maintaining a second universal type catalog.

## Placement Rules

Use one domain-core/role layout in combined and split deployments. Keep leaf folders flat and rely on prefix naming before introducing extra subfolders. The [unified architecture](../../monica-application/references/03-unified-architecture.md) owns project boundaries.

| Building Block | Placement |
| --- | --- |
| ApplicationRequestHandler commands and queries | `Domains/{Domain}/Roles.{Domain}[.{Purpose}]/{HandlersCommand,HandlersQuery}/` |
| Local event and external subscription entry points | The owning role's `HandlersEvent/` or `Subscribers/`, keeping local invariant behavior distinct from autonomous consumption |
| RecurringJob / TriggeredJob | The owning role's `BackgroundWorkers/` or `Jobs/` |
| Entity, value object and DomainService | `Domains/{Domain}/Domains.{Domain}/{Entities,ValueObjects,DomainServices}/` |
| Repository interface | `Domains/{Domain}/Domains.{Domain}/Interfaces/` |
| Repository implementation, DbContext and EF mapping | `Domains/{Domain}/Domains.{Domain}/Repository/` |
| Shared domain options | The domain core's `Configurations/` |
| Role-local integration options | The owning role's `Configurations/` |
| Whole-host options | The host's `Configurations/`, consumed by its `Composition/` |
| Published request, DTO and event | `Shared/Platform.Protocol/PublishedLanguages/Domain{Domain}/{Requests,Models,Events}/` |
| Pure helper | The owning core or role's `Utilities/` |
| Real migrations and snapshots | Top-level `Migrations/Migrations.Domain{Domain}/` |

Application EventBus contracts, including shared technical envelopes, use `Event*` names and inherit `Monica.EventBus.Events.DomainEvent`. Place them in the owning published language's `Events/` folder, separate from domain implementation and persistence entities. Generic synchronization envelopes belong to the solution's shared system published language; business-specific facts belong to their business subdomain. Open generic contract types are excluded from Building Block discovery; closed payloads referenced by discovered handlers become separate building blocks when their generic definition is in the host's discovery scope. Include the contract assembly as well as the handler assembly.

Use an unversioned CLR name for the initial event contract. Add a version suffix only when introducing an incompatible contract revision; delivery configuration or relocation alone does not require one.

## Folder Scanability Rules

- Prefer prefix naming inside a folder instead of adding sub-folders with one or two files.
- Keep leaf folders directly recognizable in the IDE: `QueryHandlerGetOrder.cs`, `CommandHandlerApproveOrder.cs`, `DistributedEventHandlerOrderApproved.cs`, `WorkerRefreshOrderSnapshot.cs`.
- Use `HandlersEvent` as the standard event-handler folder name. Do not alternate between `HandlersEvent` and `EventHandlers`.
- Keep AppHost or gateway projects composition-only. Registration extensions and host adapters are allowed there; business Building Blocks and domain behavior stay in their owners. Do not introduce a Monica module per business feature.
- Put pure helper code in `Utilities/` with `Utils*` names. If a type starts accumulating orchestration or domain rules, move it into a `DomainService` or entity instead.

## Boundary Rules

- Keep `Result` and `Result<T>` at public application/Facade boundaries. Internal domain services and repositories use normal return types and exceptions.
- Keep orchestration in handlers and business rules at their state/domain owner. Handler length alone does not justify a DomainService; select one only for a meaningful domain responsibility.
- Keep entity invariants on the entity. Do not let handlers or repositories directly toggle domain state through scattered property assignments.
- Keep repository interfaces on the domain side of the boundary and repository implementations on the infrastructure side.
- The domain core owns persistence in every deployment: implementations stay in `Domains/{Domain}/Domains.{Domain}/Repository/`; role libraries consume their narrow interfaces.
- Keep libraries that are only used by one repository or adapter in the owning subdomain/service project. Do not move that implementation into `Platform` unless the implementation itself is project-common reusable infrastructure.
- Keep request, response, and event contracts separate from persistence entities.
- Keep endpoint metadata on the request contract. the single ApiEndpoint declares the operation and its HTTP mapping; optional shared wire settings stay with its assembly; generated `ApplicationRequestHandler` handlers do not carry MVC endpoint or binding attributes.
- Use `DateTime` for timezone-free wall-clock contract values and `DateTimeOffset` for instants or explicit offsets. Do not compensate for transport with ad hoc UTC/local conversions in handlers.
- Publish a request to API clients only through the explicit `*.PublishedLanguages.Domain{DomainName}.Requests` namespace boundary. Local HTTP requests stay outside that namespace even when they reuse published DTOs.
- Keep adapter code thin. Generated endpoint registration, API transports, or background-host wiring should not replace Building Blocks.

## Practical Heuristics

- Consider a `DomainService` when a meaningful domain rule has no natural entity owner; reuse is supporting evidence, not a reason to move an entity's invariant out of it.
- Add a value object when a concept has rules but no identity.
- Add a new repository method only for meaningful domain queries. Do not wrap every basic CRUD call in a bespoke method.
- Add a new event only when another building block can react without depending on the caller's internal flow.

## Result Handling

- For typed success results, use `return Result.Success(data);`, including string data. Use an explicit type argument when inference cannot express the declared payload type.
- For typed failure results, use `return Result.Failure<TData>("message", status);`; no-data failures use `Result.Failure`. Forward an existing failure with `Result.FromFailure<TData>(source)` to preserve its status and metadata.
- Messages use `WithMessage`. Do not return bare data or strings, erase payloads implicitly, or infer non-null data from success. Follow the [result contract](../../monica-development/references/result-type-guide.md).
