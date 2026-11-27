# Adapters, Lifecycle Building Blocks, and Support Types

These choices supplement the [building block catalog](00-building-block-overview.md) within the unified domain/role architecture. Apply any project-specific convention for these types. Native discovery support does not authorize using an adapter to bypass domain invariants or inventing a new role.

## Bounded CRUD

`CrudService<TEntity, TKey, TDetail, TRow, TQuery>` implements `ICrudApplicationService` and is classified as `CrudApplicationService`. Use it for bounded persistence-facing CRUD. The feature owns the detail conversion, row projection, entity construction, and tracked updates. Declare the write actions that the resource supports. Use request handlers and entity methods to enforce the invariants of commands with meaningful state transitions.

This adapter may consume the same concrete repository's `IEfEntityStore` view because the native CRUD contract requires EF access. The [repository rules](13-repository-and-persistence-template.md) bound this exception: ordinary handlers, jobs, and domain services still inject `IRepository{Aggregate}`. Do not widen the business interface or add a forwarding repository layer.

Place the concrete service at `Roles.{Domain}/CrudServices/CrudService{Resource}.cs`. Explicit HTTP controllers live under `Controllers/`.
Place shared conversions at `Domains.{Domain}/Mappers/Mapper{Subject}.cs`. Expose a named static method for each operation a caller uses.
For example, `ToDetail` maps a materialized entity and `ProjectRows` projects an `IQueryable` for database execution. Add `Create` or `ApplyUpdate` when a write path uses that conversion.
Use a Mapperly `partial` method for a complete structural copy or a provider-translatable projection. When an in-memory conversion selects a deliberate subset, write a C# initializer that names the fields it copies. Keep pure business conversions beside the generated methods.
Share common Mapperly settings through assembly defaults. Keep exceptions for null assignment, cloning and service-owned write fields explicit where they apply. I/O, authorization and transactions belong in the service.
See [typed CRUD services](../../monica-infra-web/references/endpoints-and-execution.md#typed-crud-services) for the action and projection contracts and [transactions](../../monica-infra-persistence/references/transactions.md) for execution.

## Startup Seeder

Use `ISeeder`, normally `SeederBase`, for finite, idempotent preparation with dependency/readiness ordering. It is a discovered `Seeder`; the framework imposes no prefix convention. Place work with its domain or role owner; a core's autonomous work uses its declared same-domain BackgroundRole. The graph retains Manual/Held and Disabled definitions for diagnosis. Manual execution runs the selected target and its unsatisfied eligible prerequisites; Disabled prerequisites reject admission. A finite process-memory initializer needed by included passive behavior may use ProcessLocal, but cannot automatically start a held dependency. Keep domain writes through their owning repositories and the established execution scope.

Use [startup seeding](../../monica-infra-jobs/references/startup-seeding.md) for registration, dependencies, policy, retries, and readiness. A repeating task is a recurring job; a perpetual connection or listener is a hosted service. Do not build a second startup coordinator.

## Hosted Service

Follow the [hosted-service template](18-hosted-service-template.md) for naming, role placement, scoped work, admission, ordering, cancellation, and verification. A permanent loop must not be started by a Seeder.

Use a hosted service when work owns host start/stop or continuous infrastructure activity. `IHostedService` is discoverable as `HostedService`; Monica's `MoHostedService` and `MoBackgroundService` additionally supply observed lifecycle and finite work-item execution points. Use [hosted-service integration](../../monica-infra-hosting/references/hosted-services.md) for registration and scope/lifecycle semantics.

Register one host-owned instance. Do not retain scoped repositories in a long-lived worker; finite work runs in the native work-item scope. Business behavior stays in its owning entity/service. Choose a scheduler job when the requirement is independent scheduled/triggered execution with history and operator control.

## Controller and Internal Mediator Handler

Request-owned `ApplicationRequestHandler` endpoints are the normal HTTP use-case shape. A handwritten `ControllerBase` is a discovered `HttpApi` transport adapter when HTTP-specific behavior warrants it. Keep it thin and in the delivery/application boundary, with domain rules elsewhere. Generated mediated controllers remain generator-owned; do not duplicate them or attach MVC attributes to their handlers.

For an internal operation, implement `Monica.Core.Mediator.IRequestHandler<TRequest, TResult>` with the normal transient dependency marker. It has no HTTP endpoint and is not classified as `ApplicationRequestHandler` merely because its name contains `Handler`. An `IResultRequest` input remains a discoverable `RequestDto`; a plain mediator `IRequest` does not. Follow the [application request-handler rules](10-application-request-handler-template.md) and [execution guidance](../../monica-infra-web/references/endpoints-and-execution.md) for the actual invocation boundary. An `ApplicationRequestHandler` may omit `[ApiEndpoint]` to remain local while retaining its ApplicationRequestHandler kind. A plain handler remains available for a deliberately internal custom boundary. Neither shape activates the pipeline through a direct method call.

## Facade and Other Support Types

Business UI calls application use cases through the [application invocation boundary](../../monica-application-ui/references/application-invocation.md). Framework diagnostic Facades remain infrastructure APIs; do not create forwarding Facades for business pages. Existing Facades are migrated by responsibility: display aggregation belongs in presentation, use-case orchestration in handlers, and business rules in the domain. UI never gains access to persistence internals. Preserve the public result boundary while internal services use ordinary .NET returns and exceptions. A Facade alone has no discovered kind; its actual inheritance/contracts determine discovery.

Value objects, DTOs, mappers, validators, pure helpers, and composition modules are allowed support types when they have a concrete responsibility. Keep them at that responsibility's owner. Composition-only hosts may contain modules and registration adapters; this permits wiring, not domain behavior. A plain DI-registered `Operation`, `Processor`, or `Coordinator` must not take over work that an existing handler, entity, or [domain service](11-domain-service-template.md) can own. Judge a proposed type by its behavior, including when it is private to one feature. If a requirement needs a new building block contract, identify the limitation of the existing composition and follow [specification evolution](../../monica-application/references/01-specification-evolution.md) before introducing it. Do not introduce a support-type base family, service toolbox, scanner, or alternate provider/transaction framework without that decision.
