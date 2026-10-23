---
name: monica-application-unit-testing
description: Apply Monica application testing rules when naming or migrating test projects, creating or reviewing behavior tests, host scenarios, persistence isolation, and provider seams. Load monica-application and the repository's project specification first.
---

# Monica Application Unit Testing

Load `$monica-application` and the repository-designated project specification before test design or edits, including direct invocation of this skill. Use its validation contract and adopted architecture/unit rules. The [shared specification](../monica-application/references/00-application-specification.md) defines rule strength; use [specification evolution](../monica-application/references/01-specification-evolution.md) before introducing a new testing or execution model.

Test business behavior through a complete Monica host when composition matters. Each scenario owns its host and database; registrations are finalized before build. Arrange, act and assert use independent scopes.

For domain, role, shared-library, host, and test-support naming or migration, read [test project ownership and naming](references/project-naming.md). The project specification owns the concrete migration map.

## Repository Test Infrastructure First

Search the repository's shared test layer (for example Platform.Testing or PlatformTestApplicationFactory, including an existing library awaiting rename) before writing a factory. Extend existing project infrastructure when it covers the scenario. Do not introduce another framework with different transaction semantics.

## Workflow

1. Inspect production startup, module registration, discovery assemblies, contexts, external adapters and existing test factories.
2. Select the behavior's production owner and use its runnable test project according to the naming reference.
3. Derive a stateless project recipe from MonicaTestApplicationFactory<TDiscoveryAnchor>:
   - ConfigureMonica composes the relevant production module graph.
   - TypeDiscoveryAssemblies includes all production assemblies needed by the scenario.
   - ConfigureHost supplies host/environment inputs.
   - ConfigureServices applies stable boundary replacements before build; call its base implementation.
4. Call CreateAsync for each scenario. Its optional ISeamReplacementBuilder callback applies scenario-specific replacements before build.
5. Arrange with application.SeedAsync<TContext,TResult>, execute with application.ExecuteAsync, and verify with application.VerifyAsync<TContext>.
6. Assert business outcomes, persisted state and durable notifications separately from delivery.
7. Dispose the application and run the target test project.

## Write and Read Boundaries

Use `$monica-infra-persistence` for the current repository and UnitOfWork contracts; this section identifies the test boundary those contracts require.

- IRepository<TEntity> exposes Add/Remove; keyed repositories load tracked aggregates with FindAsync/GetAsync. Mutate the loaded instance. No detached Update or repository save is required.
- application.ExecuteAsync creates a fresh scope and calls the production IExecutionPipeline. Resolve the handler inside its callback. Exceptions, cancellation and failed Monica result envelopes roll back; an early FlushAsync or direct context save stays inside that transaction.
- AddRepositoryDbContext defaults to UnitOfWork participation and enables the module. Independently managed infrastructure stores explicitly use Default. Multiple business contexts require explicit operation selection.
- Completed/faulted operation scopes are terminal. Each retry or independent operation gets a fresh scope. A caught nested failure still prevents commit.
- For mediator-specific behavior, resolve IMediator in a fresh scope and Send the actual request. Published Query requests are read-only by application convention regardless of HTTP method and avoid an automatic write transaction.
- For direct read-only calls, use application.CreateScope and resolve the service there. Direct resolution alone does not invoke the pipeline.
- Native EF queries stay in infrastructure; business query interfaces return materialized projections. Do not enumerate an IQueryable after its scope is disposed.

## Arrange and Verify

application.SeedAsync<TContext,TResult> takes a typed context callback. Add a heterogeneous graph together, call db.SaveChangesAsync inside the callback, and return keys or DTOs. An unsaved callback fails visibly.

Each seed callback and VerifyAsync callback uses a fresh scope. Data remains in the scenario database across these scopes. Pass foreign keys or load existing parents when arranging later data; do not pass detached seed graphs into the act phase.

Do not initialize contexts manually, clear trackers to make writes pass, or reconstruct transaction behavior in test helpers.

## Database and Audit

UseTestDatabase<TContext>() overrides provider options while preserving production context/provider registrations. It requires production AddRepositoryDbContext first. One named SQLite memory database is owned by each scenario/context type, with independent scoped connections. Optional configureOptions supplies necessary EF interceptors/options.

UseRealTestDatabase<TContext> for production-provider SQL, isolation and sharding tests. The caller owns schema/data isolation and cleanup. SQLite does not establish those guarantees.

Default seams retain the real audit policy. Replace ICurrentUser, TimeProvider and ID generators when deterministic inputs are needed; do not replace audit logic with a no-op.

## Event Assertions

- Explicit domain effects use IDomainEventQueue and same-scope IDomainEventHandler handlers before commit.
- Committed notifications/integration events use ordinary `[Outbox]` EventBus payloads. Inspect persisted JSON snapshots before delivery.
- Call application.DrainOutboxAsync<TContext> explicitly to test delivery. Keep the scoped bus gateway and replace `IEventTransport` with a recording double.
- Delivery is at least once. Exercise a failure after receipt but before acknowledgment and assert stable message identity and optional `[Inbox]` consumer deduplication.
- Do not restore pre-commit transport publishing or sleeps to wait for an uncontrolled dispatcher.

## Ownership and Naming

- Project identity and cross-project scenario placement follow the [naming reference](references/project-naming.md).
- Factory: {Boundary}TestApplicationFactory; field: _factory.
- Classes: {TypeUnderTest}Tests; methods: Method_WhenCondition_ShouldExpectation.
- Mirror production folders such as HandlersCommand, HandlersQuery, DomainServices, Repositories, Entities and Modules.
- Keep Builders, TestDoubles, TestData and Factories test-only.
- Do not cache an application/provider on a reusable factory.
- A built service provider is immutable. CreateScope never replaces registrations.
- Never share a MonicaApplication across root providers.
- Independent scenarios run in parallel. Serialize only a named real external resource that cannot be isolated.

## Requirement and Unit Traits

- Declare the governing requirement once per test class with `[Trait("REQ", "<requirement-id>")]`; add a method-level REQ trait only when one test exercises an additional requirement.
- Add `[Trait("Unit", "<project-relative-path>::<runtime-key>")]` using the project-qualified catalog reference when `{TypeUnderTest}Tests` is absent or ambiguous. Class traits apply to every method; method traits add associations only for that method. Explicit Unit traits take precedence over naming inference.
- `REQ` and `Unit` declare separate relationships. A unit association does not imply verification of every requirement on that unit. `dotnet test --filter "REQ=<requirement-id>"` selects declared tests; verify the selected count and execution result before claiming coverage. Runtime result formats may expose traits for CI cross-checks.
- Trait arguments must be constant, non-empty strings. Requirement IDs must resolve to current Knowledge; use the allocated opaque identity, not a historical title or rewritten ID. Other trait keys stay free for repository-local tooling.

## Smaller Boundaries

Use raw ProjectUnitFixture<TUnit> only when collaborators are explicit and composition, discovery, proxies, transactions, hosted lifecycle and host ownership are outside the assertion. It does not supply a substitute production UoW. Entity/value-object invariant tests may construct objects directly.

Replace external boundaries, not domain services, repositories or policy logic. Avoid uncontrolled network/database access and hidden machine dependencies. Keep assertions in tests.

## References and Validation

Read references/standards.md for layout and ownership, references/templates.md for exact API examples, and references/database-isolation.md for provider choices.

Run the relevant test project using the repository's verification policy and paths understood by the selected SDK. Keep build/test processes sharing outputs sequential and resolve all build warnings.

For adopted application UI, verify sequential and concurrent operation scopes, current circuit identity (including explicit anonymous callers), nested mediator scope preservation, validation/authorization parity with HTTP, rollback and cancellation through a real host. Use browser smoke checks for layout, themes and route refresh; UI rendering tests remain opt-in.
