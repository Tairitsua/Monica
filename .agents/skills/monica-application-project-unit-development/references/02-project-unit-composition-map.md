# ProjectUnit Composition Map

Use this file to select units within the approved application specification. The feature's state and execution requirement determine the shape. Do not add a service, event, scheduler, or delivery policy speculatively.

The selections below are composable examples, not a closed catalog of permitted business flows. Combine the units that the requirement needs while preserving their responsibilities and documented execution contracts. Apply [specification evolution](../../monica-application/references/01-specification-evolution.md) only for a concrete architectural gap or rule change that composition cannot resolve.

For a flow involving slow external I/O and durable writes, first select the existing [transaction boundaries](../../monica-infra-persistence/references/transactions.md) and [delivery capabilities](../../monica-infra-persistence/references/transactional-events.md). A nontransactional orchestrator can coordinate independent local write operations; durable event handling can drive later work. Moving the whole slow flow into a transactional event handler does not shorten its transaction. Add a job only when the work needs its independent scheduling and execution lifecycle.

## 1. New query use case

Create:

- `RequestDto`
- `ApplicationService`
- Optional `Repository` method for non-trivial querying
- Optional `DomainService` for a meaningful domain policy with no natural state owner

Load:

- [10-application-service-template.md](10-application-service-template.md)
- [12-entity-request-event-template.md](12-entity-request-event-template.md)
- [13-repository-and-persistence-template.md](13-repository-and-persistence-template.md) when persistence changes are needed

## 2. New command use case

Create:

- `RequestDto`
- `ApplicationService`
- Update or add `Entity`
- Optional `Repository` for tracked loading and staging
- Optional `DomainService` for a meaningful rule spanning aggregates or with no natural entity owner
- Optional `DomainEvent` when another unit needs the resulting fact

Load:

- [10-application-service-template.md](10-application-service-template.md)
- [12-entity-request-event-template.md](12-entity-request-event-template.md)
- [13-repository-and-persistence-template.md](13-repository-and-persistence-template.md)
- [11-domain-service-template.md](11-domain-service-template.md) only when such a domain rule exists
- [17-connected-use-case-example.md](17-connected-use-case-example.md) for a handler that loads an aggregate and calls its invariant directly

## 3. New aggregate or persistent entity

Create:

- `Entity`
- `Repository` interface and implementation
- Persistence mapping or `DbContext` update
- Optional `ApplicationService` if the aggregate also gets a new use case

Load:

- [12-entity-request-event-template.md](12-entity-request-event-template.md)
- [13-repository-and-persistence-template.md](13-repository-and-persistence-template.md)

## 4. New event-driven reaction

Create:

- `DomainEvent`
- `DomainEventHandler` or `LocalEventHandler`
- Optional `DomainService` if the reaction contains reusable business logic
- Optional `TriggeredJob` if the work needs the [independent execution lifecycle](15-job-template.md) of a job

Load:

- [12-entity-request-event-template.md](12-entity-request-event-template.md)
- [14-event-handler-template.md](14-event-handler-template.md)
- [15-job-template.md](15-job-template.md) when the reaction becomes queued or delayed

## 5. New scheduled or independently managed background execution

Create:

- `RecurringJob` for cron-like scheduled work
- `TriggeredJob` for on-demand background work
- Existing entity/domain behavior, with an optional `DomainService` only when its responsibility warrants one
- Optional `Configuration` for schedule or behavior tuning

Load:

- [11-domain-service-template.md](11-domain-service-template.md)
- [15-job-template.md](15-job-template.md)
- [16-configuration-template.md](16-configuration-template.md)

## 7. Adapter, startup, or host lifecycle work

Use [04-adapters-lifecycle-and-support-types.md](04-adapters-lifecycle-and-support-types.md):

- Bounded, persistence-facing generic CRUD: the native CRUD adapter under its explicit exception.
- Finite startup preparation: a Seeder.
- Continuous infrastructure or start/stop ownership: a [hosted service](18-hosted-service-template.md) with finite scoped work where required.
- HTTP-specific transport behavior: a thin controller only when a request-owned endpoint does not express it.
- Internal mediated operation: the existing `IRequestHandler` contract without HTTP exposure.
- Public business UI/API entry: an ApplicationService reached through the generated domain client.

These choices preserve existing business roles. Use the native mechanism and the project's adopted placement; a different combination of these adapters does not itself require specification evolution.

## 6. New configurable behavior

Create:

- An `Options{Purpose}` Configuration ProjectUnit beside its consumer, with Startup or Managed ownership.
- Runtime consumers using the appropriate default-name `IOptions<T>`, `IOptionsSnapshot<T>`, or `IOptionsMonitor<T>` interface.
- Composition reads Startup options through `GetStartupOptions<T>()` on its Monica builder or registration context; Managed options are never read before DI. Keep Application identity/placement in its canonical section.

Load:

- [16-configuration-template.md](16-configuration-template.md)
