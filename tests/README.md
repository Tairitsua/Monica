# Monica Testing

This directory contains Monica's runnable test projects and shared execution conventions. The testing model separates direct logic tests, raw ProjectUnit collaboration tests, complete host-owned scenarios, and Blazor component tests so each assertion uses the correct runtime boundary.

## Scope

- `Monica.Testing` provides host-owned scenario infrastructure, raw ProjectUnit fixtures, result assertions, repository helpers, and deterministic boundary doubles.
- `Monica.Testing.UI` provides reusable UI test support without adding UI dependencies to the core toolkit.
- `tests/Test.Monica.*` contains runnable framework test projects.
- Real external integration, browser automation, and end-to-end suites are outside this unit-test layer.

## Choosing a Test Boundary

| Behavior | Boundary |
|---|---|
| Value object, parser, validator, or deterministic support logic | Construct directly |
| One ProjectUnit with explicit collaborators | Raw `ProjectUnitFixture<TUnit>` |
| Module graph, options, registration, hosted lifecycle, or cross-scope behavior | Full `MonicaTestApplicationFactory<TDiscoveryAnchor>` scenario |
| Blazor component or page shell | bUnit in a UI test project |
| Source-generator input, diagnostics, generated source, or same-compilation binding | In-memory Roslyn `GeneratorDriver` |

`ProjectUnitFixture<TUnit>` uses a small raw Microsoft DI container. It does not prove Monica type discovery, conventional registration, module options, hosted lifecycle, or host ownership. There is no separate `ApplicationServiceFixture<THandler>`; apply the same boundary decision to application services as to every other ProjectUnit.

## Host-Owned Scenario Model

Create a reusable project factory:

```csharp
public sealed class JobSchedulerTestApplicationFactory
    : MonicaTestApplicationFactory<TestRecurringJob>
{
    protected override void ConfigureMonica(IMonicaBuilder monica)
    {
        monica.AddJobScheduler(options => options.ProjectName = "Test.Monica.JobScheduler")
            .UseSchedulerScope("job-tests")
            .UseInMemoryStore();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        // Register stable test providers shared by every scenario from this factory.
    }
}
```

Create one complete application per test scenario:

```csharp
var externalClient = Substitute.For<IExternalClient>();

await using var application = await _factory.CreateAsync(
    scenario => scenario.With<IExternalClient>(externalClient),
    TestContext.Current.CancellationToken);
await using var scope = application.CreateScope(TestContext.Current.CancellationToken);

var service = scope.Resolve<MyApplicationService>();
var result = await service.ExecuteAsync(scope.CancellationToken);
```

`CreateAsync(...)` builds and starts a distinct host with its own `MonicaApplication`, module graph, singleton services, and disposal boundary. The optional scenario callback changes the service collection before build. `CreateScope(...)` creates a normal child scope and never replaces registrations.

Use `ConfigureHost(WebApplicationBuilder)` for test environment or configuration inputs, `ConfigureMonica(IMonicaBuilder)` for the real module composition, and `ConfigureServices(IServiceCollection)` for stable test boundaries. Use another `CreateAsync(...)` call when registrations differ between scenarios.

## Isolation and Parallelism

- Independent scenarios run in parallel by default because Monica composition is host-owned.
- Keep stateful doubles, databases, files, ports, topics, queues, and IDs owned or uniquely named by a scenario.
- Use xUnit collections only for a named external resource that cannot be isolated. Document the resource and why serialization is required.
- Dispose every `MonicaTestScope` before its `MonicaTestApplication`.
- Never copy descriptors from a built provider, build a second root around an existing `MonicaApplication`, or mutate registrations while creating a scope.
- Do not start multiple independent `dotnet build` or `dotnet test` processes in WSL. Test-runner parallelism inside one process remains allowed.

## Repository Scenarios

Use production context/session registration. UseTestDatabase supplies a scenario-owned database, not a fresh database per scope. SeedAsync<TContext,TResult> arranges and saves explicitly, ExecuteAsync runs the production pipeline in a fresh act scope, and VerifyAsync<TContext> asserts through a third context. Retain real audit policy and replace its clock/user inputs. Capture and drain outbox notifications separately.

## Directory and Naming Rules

- Shared core toolkit: `Monica.Testing/Monica.Testing.csproj`
- Shared UI toolkit: `Monica.Testing.UI/Monica.Testing.UI.csproj`
- Runnable framework project: `tests/Test.Monica.{ProjectName}/`
- Test class: `{TypeName}Tests`
- Test method: `Method_WhenCondition_ShouldExpectation`
- Project factory: `{ProjectName}TestApplicationFactory`

Mirror source folders such as `Modules/`, `Facades/`, `Services/`, `Providers/`, `Pages/`, and `Components/`. Keep scenario-only builders, doubles, and data in clearly named support folders within the runnable project.

## Stack

- xUnit v3
- AwesomeAssertions
- NSubstitute with `NSubstitute.Analyzers.CSharp`
- coverlet.collector
- bUnit only in UI test projects

## Monica-Specific Assertions

- Prefer public-surface behavior.
- Assert facade `Res` or `Res<T>` status, message, and data explicitly.
- Always assert successful `Res<string>.Data` to catch the `Res.Ok(string)` overload trap.
- Test module registration extensions through dependencies, options, DI-visible registrations, or runtime snapshots rather than static tables alone.
- Avoid real network, uncontrolled persistence, sleeps, random/manual output, and hidden developer-machine state.

## Adding a Runnable Test Project

1. Create `tests/Test.Monica.{ProjectName}` and mirror the production project name.
2. Reference `Monica.Testing` and the production project.
3. Reference `Monica.Testing.UI` and bUnit only for UI tests.
4. Add a `MonicaTestApplicationFactory<TDiscoveryAnchor>` only when host composition is part of the behavior.
5. Add direct or raw ProjectUnit tests where the smaller boundary is honest.
6. Keep scenario-specific seams in the runnable project; promote only genuinely reusable infrastructure to the shared toolkit.

Source-generator tests are the exception to the host and ProjectUnit boundaries above. Keep them in a dedicated `Test.Monica.{GeneratorProject}` project, instantiate the incremental generator directly, and run it against an in-memory `CSharpCompilation`. Assert the updated compilation as well as generated text so same-compilation references are proven. Repeated identical runs must remain deterministic and must not create repository files; reserve solution-build tests for behavior owned by analyzer packaging or MSBuild targets.

## Opt-in native lease tests and measurements

`Test.Monica.JobScheduler/Stores/External` contains native protocol and shared lease integration scenarios tagged `Category=ExternalDatabase`. They skip unless the explicit `MONICA_JOBSCHEDULER_TEST_POSTGRES` or `MONICA_JOBSCHEDULER_TEST_VASTBASE` connection setting is present. Use a dedicated test server with database create/drop privileges: the fixture creates uniquely named `monica_jobs_test_*` databases, caps the shared application pool at three connections, and removes only its own database. It never reads application connection configuration. The Vastbase fixture requires the actual supported G100 Build15 server; PostgreSQL is not a substitute for that validation.

```powershell
# Set the test connection through your environment before running these commands.
dotnet test tests/Test.Monica.JobScheduler/Test.Monica.JobScheduler.csproj -c Release --no-build --filter 'Category=ExternalDatabase'

# Optional PostgreSQL measurements; use existing absolute output directories.
$env:MONICA_LEASE_PERF_OUTPUT = Join-Path $PWD 'coordinator-measurements.json'
$env:MONICA_OUTBOX_PERF_OUTPUT = Join-Path $PWD 'outbox-measurements.json'
dotnet test tests/Test.Monica.JobScheduler/Test.Monica.JobScheduler.csproj -c Release --no-build --filter 'FullyQualifiedName~ExternalCoordinatorPerformanceTests|FullyQualifiedName~Maintenance_WhenManyHostsShareTheTable'
# Set MONICA_LEASE_PERF_UNRELATED_ROWS=10000 for the larger shared-table scenario.
```

The coordinator workload uses 1/4/16 store instances and 100 measured operations per instance after warmup. It records SQL commands, transactions, and latency percentiles. The Outbox workload uses independent real hosts sharing one native database, 4,000 expired plus 12,000 retained rows, and 512-byte bodies. It compares the previous uncoordinated cleanup algorithm (test-only reference) with the worker's first coordinated pass, including election, and verifies one status sample across replicas. Treat these as bounded regression measurements, not production sizing or steady-state throughput claims. See the recorded [shared-lease performance report](performance/shared-leases-2026-10-27/README.md).

## WSL Execution

Use Windows paths:

```bash
pwsh.exe -File 'D:\Code\MoLibrary\scripts\run-tests.ps1' -NoBuild
```

For coverage:

```bash
pwsh.exe -File 'D:\Code\MoLibrary\scripts\run-tests.ps1' -NoBuild --collect:"XPlat Code Coverage"
```


### Redis leases and maintenance authority comparison

Set `MONICA_LEASE_TEST_REDIS` to a disposable Redis endpoint (for example `127.0.0.1:<owned-port>`). The Redis contract suite uses unique key namespaces; normal grants expire or are conditionally released. The discovery suite also composes two real hosts against that authority. Do not point native mutation/revocation tests at an application Redis instance.

```powershell
dotnet test tests/Test.Monica.Locker.Redis/Test.Monica.Locker.Redis.csproj -c Release --no-build
dotnet test tests/Test.Monica.ServiceDiscovery/Test.Monica.ServiceDiscovery.csproj -c Release --no-build

$env:MONICA_REDIS_LEASE_PERF_OUTPUT = 'C:/test-results/coordinator.json'
$env:MONICA_ADVISORY_OUTBOX_PERF_OUTPUT = 'C:/test-results/outbox.json'
dotnet test tests/Test.Monica.JobScheduler/Test.Monica.JobScheduler.csproj -c Release --no-build --filter 'FullyQualifiedName~NativeAuthorities_ShouldRecord|FullyQualifiedName~MaintenanceAuthorities_ShouldCompare'
```

The comparison also requires `MONICA_JOBSCHEDULER_TEST_POSTGRES` as above. Coordinator scenarios use 1/4/16 independent stores and Redis connections, 100 operations per replica, and both three-connection and sixteen-connection PostgreSQL pools. The latter writes a `.pool16.json` sibling. SQL interceptors and the Redis client profiler count actual commands. These paths use the same shared lease protocol, without a StateStore serialization adapter.

Outbox scenarios use real hosts, real authority/elapsed clocks, 4,000 expired and 12,000 recent acknowledgments with 512-byte payloads. Only maintenance cadence is triggered manually. The fixture truncates/reseeds its owned table and refreshes statistics between samples, outside measured intervals. It records first election plus cleanup and five cached-holder cleanup passes, checks one status query across replicas, and preserves all recent rows. Standby traffic is measured separately in the coordinator workload. Cleanup uses the production two-second pass budget and five-second operation deadline. A separate native test revokes Redis while a DELETE is in flight, verifies safe overlapping cleanup, and then delivers the preserved pending message.

These measurements do not establish Redis failover exclusivity, WAN latency, production throughput or deployment sizing. See [Redis lease measurements](performance/redis-leases-2026-10-28/README.md) for conditions and raw results.
