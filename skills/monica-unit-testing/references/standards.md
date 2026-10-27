# Monica Testing Standards

## Layout

- Keep runnable tests under `tests/`.
- Put shared test infrastructure in `Monica.Testing/`.
- Name runnable framework projects `Test.Monica.*` and mirror the source layout inside them.

## Boundary Selection

| Behavior under test | Test boundary |
|---|---|
| Value objects, parsers, validators, deterministic support code | Direct construction |
| One ProjectUnit with every collaborator supplied explicitly | Raw `ProjectUnitFixture<TUnit>` |
| Module graph, options, DI registration, proxies, hosted lifecycle, or cross-scope behavior | `MonicaTestApplicationFactory<TDiscoveryAnchor>` scenario |
| Blazor behavior contract, when UI testing is explicitly requested | bUnit in the UI test project |
| Incremental source-generator input, diagnostic, or generated source | In-memory Roslyn `GeneratorDriver` |

`ProjectUnitFixture<TUnit>` does not validate Monica composition. Do not add a separate `ApplicationServiceFixture`; application services use either the raw ProjectUnit fast path or a real scenario host according to the behavior being tested.

## Source-Generator Isolation

- Build source-generator scenarios from in-memory `CSharpCompilation` instances and explicit `MetadataReference` values.
- Instantiate the public incremental generator and adapt it with `AsSourceGenerator()` for `CSharpGeneratorDriver`.
- Assert both `GeneratorDriverRunResult` and the updated compilation. Generated text alone cannot prove that same-compilation references bind.
- Compare a repeated run using identical inputs to prove deterministic hint names, source text, and diagnostics.
- Keep semantic generator tests free from solution builds, analyzer package imports, generated-source output folders, and repository file writes.
- Use MSBuild integration tests only for behavior actually owned by `.props`, `.targets`, packaging, or post-compilation tasks.

## Scenario Ownership

- Treat one `MonicaTestApplicationFactory.CreateAsync(...)` call as one test scenario.
- Give every scenario its own complete host and `MonicaApplication`.
- Apply module and service registrations before host build.
- Apply stable project seams in factory `ConfigureServices(...)`.
- Apply test-specific seams in the `CreateAsync(...)` callback.
- Create child scopes with `MonicaTestApplication.CreateScope(...)`; never use scopes to replace registrations.
- Dispose scopes before disposing their application.
- Never copy a built host's service descriptors into a second root provider.

## Isolation and Parallelism

- Run independent scenarios in parallel by default; host-owned Monica state does not require global serialization.
- Keep stateful doubles owned by the scenario host or a scenario scope.
- Use unique database names, ports, directories, topics, and queue names when a test touches external state.
- Serialize only a named shared external resource that cannot be isolated. Document the reason on that collection or fixture.
- Add contract coverage for two simultaneous hosts when changing module composition, logging ownership, type discovery, or singleton lifetimes.

## Core Assertions

Use `Monica.Testing.Time.ControllableTimeProvider` for deadline/renewal behavior shared across workers. `WaitForTimerAsync` waits for a scheduled absolute elapsed-time deadline before advancing; `Advance` delivers due timers in order, `AdvanceAfterPause` models delayed timer delivery, and `AdjustWallClock` changes wall time without changing monotonic elapsed time. Await explicit worker/store barriers as well as timers: advancing the clock does not drain asynchronous continuations. See `tests/Test.Monica.Locker/Leases/LeaseCoordinatorTests.cs` for independent renewal, late responses, clock movement, and shutdown cases. Keep provider concurrency claims in native integration tests rather than inferring them from this clock or SQLite.

- Prefer public-surface assertions.
- Assert `Res` and `Res<T>` status, message, and data explicitly.
- Assert successful `Res<string>.Data` to catch the string-overload trap.
- Test module composition through dependencies, options, registrations, or runtime snapshots.
- Assert side effects through repositories, DbContexts, recording event buses, state stores, or other public boundaries.

## Stack

- xUnit v3
- AwesomeAssertions
- NSubstitute and `NSubstitute.Analyzers.CSharp`
- coverlet.collector
- bUnit only in UI test projects
- Microsoft.CodeAnalysis.CSharp only in generator test projects

## Execution

- Use the repository runner or the relevant test project, with paths understood by the selected SDK.
- Keep processes that share build outputs sequential; use MSBuild's internal parallelism inside a build.

## UI Verification

Run or add UI rendering tests only on explicit user request. Use browser smoke checks for ordinary UI development. When requested, cover authorization, disposal and late-result safety, no mutation on failure, or security properties such as credentials staying out of markup. Do not add assertions for layout, CSS classes, localized-key presence, or visual composition.
