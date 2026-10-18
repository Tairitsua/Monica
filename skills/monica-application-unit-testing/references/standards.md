# Sociable Application Testing Standards

## Test Shape

A sociable scenario creates a complete Monica host, replaces only boundaries before host build, and resolves production handlers, domain services, repositories, mappers, options, and module services from DI.

Use focused direct or raw ProjectUnit tests only when host composition adds no relevant behavior.

## Project Layout

Name the runnable project `Test.` plus the exact production project stem. Keep its folder, project file, assembly, and root namespace identical.

```text
Test.Roles.User/
  Test.Roles.User.csproj
  GlobalUsings.cs
  Factories/
    UserRoleTestApplicationFactory.cs
  HandlersCommand/
  HandlersQuery/
  DomainServices/
  Repositories/
  Entities/
  Modules/
  Builders/
  TestDoubles/
  TestData/
```

Use Test.Roles.User for Roles.User behavior and Test.Domains.User for domain-core behavior; retain the exact production stem for existing projects too.

## Factory Rules

- Derive one stateless project recipe from `MonicaTestApplicationFactory<TDiscoveryAnchor>`.
- Choose an anchor whose assembly contains the business types that Monica must discover.
- Override `TypeDiscoveryAssemblies` when the scenario spans multiple production assemblies; do not duplicate Monica type-discovery setup inside `ConfigureMonica`.
- Implement `ConfigureMonica(IMonicaBuilder)` with the relevant production module graph.
- Use `ConfigureHost(WebApplicationBuilder)` for configuration sources, environment, or other host-builder inputs.
- Use `ConfigureServices(IServiceCollection)` for stable test databases and boundary adapters shared by all scenarios; call `base.ConfigureServices(services)` first unless all standard seams are intentionally replaced.
- Keep project-specific double behavior in `TestDoubles/`.
- Never store a created `MonicaTestApplication`, service provider, scope, or mutable scenario state on the factory.

## Scenario Rules

- Call `factory.CreateAsync(...)` once per independently isolated scenario.
- Put test-specific seam registrations in its `ISeamReplacementBuilder` callback.
- Create normal child scopes with `application.CreateScope(...)`.
- Resolve units under test from `MonicaTestScope`.
- Run writes through application.ExecuteAsync, which creates a fresh scope and invokes the production execution pipeline. Resolve the handler inside the callback.
- Seed with application.SeedAsync<TContext,TResult>, save explicitly inside the callback and return keys. Verify persisted state through application.VerifyAsync<TContext> in another scope. Do not clear trackers to repair application tests.
- Dispose every scope before its owning application.
- Use independent arrange/act/assert scopes over the scenario database.
- Use `application.Services`, `application.Application`, or `application.ModuleSnapshots` for host-level assertions.

The service collection is immutable after build. A scope may select scoped state, but it cannot replace registrations.

## Replacement Rules

Replace leaves and adapters:

- state stores and caches
- event transports
- database connection/provider options, retaining production context/session resolution
- HTTP/RPC clients
- current user or tenant context
- local configuration adapters
- clocks, random sources, or ID generators when nondeterminism matters; retain the real audit policy

Do not replace application services, domain services, repositories, or mappers unless that type is itself the external boundary under test.

## Fast Path

Use `ProjectUnitFixture<TUnit>` only as a raw activation harness with explicit collaborators. Its assertions must not claim to cover Monica module wiring, type discovery, conventional registration, dynamic proxies, interceptors, options binding, hosted lifecycle, or host isolation.

Do not maintain a parallel `ApplicationServiceFixture<THandler>` abstraction.

## Migration Rules

- Replace copied service-descriptor roots and shared `MonicaApplication` fixtures with scenario hosts.
- Move per-scope registration replacements to the `CreateAsync(...)` callback.
- Replace shared mutable singleton doubles with scenario-owned registrations.
- Delete compatibility fixtures that manually reconstruct production DI.
- Delete tests with no assertions, manual output only, uncontrolled random loops, untracked files, or live external calls.
- Convert retained tests to xUnit v3, NSubstitute, and AwesomeAssertions.

## Trait Rules

Two trait keys are reserved for linking test classes into the ProjectUnit chain: `[ProjectUnitRequirement]` ties units to requirement IDs, and test classes declare the same IDs plus the unit under test.

- `REQ` — the governing requirement ID of the spec under test. Class-level declares the default for every test in the class; a method-level `[Trait("REQ", "...")]` adds a requirement only one test exercises.
- `Unit` — the project-qualified catalog reference `<workspace-relative-project.csproj>::<runtime-key>`. Required when the class name does not follow `{TypeUnderTest}Tests`, or when the stem names more than one unit. Runtime keys alone do not disambiguate projects.

```csharp
[Trait("REQ", "FIPS-REQ-FLIGHT-20260920-531942")]
[Trait("Unit", "src/Domains/Flight/Roles.Flight/Roles.Flight.csproj::Roles.Flight.HandlersCommand.CommandHandlerUpdateFlight")]
public sealed class FlightPlanAppServiceTests
{
    [Fact]
    public void Dispatch_WhenRunwayChanges_ShouldReplan() { }

    [Fact]
    [Trait("REQ", "FIPS-REQ-FLIGHT-20260922-000042")]
    public void Dispatch_WhenFuelIsMarginal_ShouldRequestTanker() { }
}
```

A class without Unit traits is resolved through the `{TypeUnderTest}Tests` naming convention when the stem names exactly one unit; a class with Unit traits resolves only through them. Trait arguments must be constant, non-empty strings. Keep other trait keys for repository-local tooling.

`dotnet test --filter "REQ=<requirement-id>"` runs exactly the covering slice, trait values flow into JUnit XML for CI cross-checks, and Test Explorer groups tests by requirement.

## Parallelism

Run independent scenarios in parallel because each scenario host owns its composition state.

Serialize only tests that share a named external resource that cannot be isolated. Prefer unique database names, ports, directories, topics, queue names, and containers before introducing a collection-level lock.

Class and method declarations remain separate in source analysis. Class traits apply to each method; method-level requirement and unit traits stay paired with their declaring method. Different methods never form a cross-product of requirement and unit associations. A naming-based unit association supplies navigation only and does not add undeclared requirement coverage.
