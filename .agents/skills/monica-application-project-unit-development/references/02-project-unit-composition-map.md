# ProjectUnit Composition Map

Use this file to select units within the approved application specification. The feature's state and execution requirement determine the shape. Do not add a service, event, scheduler, or delivery policy speculatively.

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

## 5. New scheduled or asynchronous workflow

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

These choices do not create new business roles. Use the native mechanism and the project's adopted placement; propose a new family or execution model through specification evolution before adopting it.

## 6. New runtime option

Create:

- `Configuration`
- Consumers that inject `IOptions<T>`, `IOptionsSnapshot<T>`, or `IOptionsMonitor<T>`
- Host-composition wiring reads bootstrap values from `builder.Configuration` and passes them into module options; Configuration ProjectUnits themselves are consumed through runtime options injection

Load:

- [16-configuration-template.md](16-configuration-template.md)
