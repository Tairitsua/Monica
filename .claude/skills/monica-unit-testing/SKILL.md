---
name: monica-unit-testing
description: Create, migrate, or review Monica framework tests and shared testing infrastructure, including host-owned scenarios, raw ProjectUnit fixtures, Roslyn source-generator tests, result assertions, and isolation.
---

# Monica Unit Testing

Use `Monica.Testing` as the shared toolkit and keep runnable framework tests under `tests/Test.Monica.*`. Choose the smallest test boundary that proves the behavior without misrepresenting Monica's activation or host lifecycle.

## Workflow

1. Read `<project-root>/tests/README.md` for the current project layout and execution rules.
2. Classify the test before choosing infrastructure:
   - Pure logic: construct the value or service directly.
   - Raw ProjectUnit collaboration: use `ProjectUnitFixture<TUnit>` and accept its activation limits.
   - Module wiring, options, conventional registration, proxies, hosted lifecycle, or cross-scope behavior: create a full host with `MonicaTestApplicationFactory<TDiscoveryAnchor>`.
   - Blazor behavior contract: use bUnit in the runnable UI test project only when the user explicitly requests UI testing or UI test additions.
   - Roslyn source-generator semantics: build an in-memory `CSharpCompilation` and run the generator through `GeneratorDriver`.
3. Put reusable assertions, host helpers, and deterministic boundary doubles in `Monica.Testing`; keep scenario-specific data and doubles in the runnable test project.
4. Prefer public-surface coverage: module registrations, facades, public models and abstractions, stable providers, and observable side effects.
5. Use the repository test runner or the relevant test project. Keep processes that share build outputs sequential.

## Host-Owned Scenarios

Use `MonicaTestApplicationFactory<TDiscoveryAnchor>` when the test depends on real Monica composition.

- Derive a project factory and implement `ConfigureMonica(IMonicaBuilder)` with the production module graph needed by the scenario.
- Use `ConfigureHost(WebApplicationBuilder)` for host configuration and environment inputs.
- Use `ConfigureServices(IServiceCollection)` for stable test-boundary registrations shared by every scenario produced by that factory. Call the base implementation first unless the scenario intentionally replaces all standard seams.
- Call `CreateAsync(...)` for each test scenario. Every call builds and starts a complete host with its own `MonicaApplication`, module graph, singleton services, logger ownership, and disposal boundary.
- Pass scenario-specific seam replacements to `CreateAsync(Action<ISeamReplacementBuilder>?)`. The callback changes registrations before the host is built.
- Call `MonicaTestApplication.CreateScope(...)` only to create a normal child scope. A built service provider is immutable; scope creation never replaces registrations.
- Dispose the `MonicaTestApplication` after the scenario, even when several scopes are used within that scenario.

Do not copy service descriptors into another root provider, share one `MonicaApplication` across providers, or emulate per-scope registration replacement.

## Persistence Scenarios

Keep production context/provider registration. UseTestDatabase overrides options and keeps one database per scenario/context type, with fresh scoped connections. Arrange with application.SeedAsync<TContext,TResult> and an explicit save; act through application.ExecuteAsync and the real IExecutionPipeline; verify in application.VerifyAsync<TContext>. Do not add synthetic save loops or clear trackers to repair tests.

Default seams retain real audit policy; replace TimeProvider/current-user/ID inputs. SeedAsync suppresses entity projections. Test `[Outbox]` publishing through the real scoped EventBus gateway, inspect committed row snapshots separately from delivery, and explicitly call DrainOutboxAsync<TContext>. Replace `IEventTransport`, not `IDistributedEventBus`, for provider recording. Include rollback after an early flush, failed results, generated-key capture, immutable payloads, retry identity, Inbox consumer uniqueness, and fresh-scope retries.

When a handler host has multiple write contexts, put `[ExecutionTransaction(ExecutionTransactionMode.Automatic, DbContextTypes = new[] { typeof(PrimaryDbContext), typeof(ParticipantDbContext) })]` on its concrete execution method and test the real receive pipeline. The selected primary context owns Inbox and Outbox rows; verify its final flush after later participants stage projections. Follow the persistence [transaction contract](../monica-infra-persistence/references/transactions.md) for selection and explicit execution features.

## Raw ProjectUnit Fast Path

`ProjectUnitFixture<TUnit>` is an explicitly raw fast-path harness. It creates a small Microsoft DI container, activates the target, and initializes Monica's cached-service-provider accessor where applicable.

Use it only when all collaborators are deliberately supplied and the assertion does not depend on:

- Monica module composition or options binding
- conventional type discovery and registration
- dynamic proxies or interceptors
- hosted-service startup and shutdown
- host-owned singleton or logging isolation

Use the host-owned scenario model for any of those behaviors. Do not introduce another application-service-specific fixture; `ApplicationServiceFixture<THandler>` is not part of the testing model.

## Roslyn Generator Boundary

Test source generators with raw Roslyn inputs under a dedicated `tests/Test.Monica.{GeneratorProject}` project.

- Reference the generator as a normal test dependency so the test can instantiate its public `IIncrementalGenerator` types. Do not rely on MSBuild analyzer execution for semantic generator tests.
- Create all source text in memory, provide explicit metadata references, and run `CSharpGeneratorDriver` with the same language version the scenario requires.
- Assert generator diagnostics, generated hint names and source, and the diagnostics of the updated output compilation.
- Run the same driver again with identical inputs and assert byte-identical generated sources and diagnostics. This protects incremental determinism.
- Verify generator execution does not create or mutate files. File export, package imports, and MSBuild target ordering belong in separate integration tests only when those build surfaces are intentionally under test.
- For request-owned Web API generation, cover the public contract matrix: route, verb, binding, operation naming, cancellation, result shapes, published-API namespace selection, local-only HTTP exclusion, duplicate or invalid declarations, and same-compilation client/controller availability.
- When a published request uses a custom `IRemoteResultEnvelope<TSelf>`, test that `CreateRemoteFailure` returns a valid constructor-initialized envelope and that cancellation is propagated rather than projected as a failure result.

## Required Conventions

- Shared toolkit: `Monica.Testing/Monica.Testing.csproj`
- Runnable project: `tests/Test.Monica.{ProjectName}/`
- Test class: `{TypeName}Tests`
- Test method: `Method_WhenCondition_ShouldExpectation`
- Test folders mirror source folders such as `Modules/`, `Facades/`, `Providers/`, `Services/`, `Pages/`, and `Components/`.

## Monica-Specific Rules

- Assert `Res<T>` explicitly. Facade tests cover `Status`, `Message`, and `Data`; successful `Res<string>` paths always verify `Data`.
- Test module behavior through registration extensions, dependencies, resulting options, or DI-visible registrations rather than static tables alone.
- Replace external boundaries, not domain logic. Resolve application services, domain services, repositories, mappers, and options from the scenario host.
- Avoid real network, uncontrolled persistence, sleeps, random inputs, and untracked machine state.
- Keep `InternalsVisibleTo` exceptional and scoped to the corresponding runnable test project.
- Run independent test scenarios in parallel by default. Serialize only tests that name and document a genuinely shared external resource.

## Default Stack

- xUnit v3
- AwesomeAssertions
- NSubstitute with its analyzer
- coverlet.collector
- bUnit in UI test projects only
- Microsoft.CodeAnalysis.CSharp in source-generator test projects only

## References

- Read `references/standards.md` for stable boundary and isolation rules.
- Read `references/samples.md` when selecting an existing framework sample.
- Use `$monica-application-unit-testing` for Monica-based business-service tests.

## Validation

- Match path syntax to the SDK process actually running.
- Keep build/test processes sharing outputs sequential.
- Run the relevant runnable test project and the repository's default non-UI gate when required. UI projects run only on explicit user request.
