# Call-chain tracing

`AddChainTracing()` registers the chain service; integrations are explicit. For controller requests and business operations, add the following inside `builder.AddMonica(...)`:

```csharp
monica.AddResultEnvelope(options => options.ExposeDiagnosticDetails = true);
monica.AddChainTracing(options =>
    {
        options.ServiceName = "orders-api";
        options.ServiceVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    })
    .UseControllerTracing()
    .UseExecutionTracing()
    .AttachControllerTraceMetadata();
```

`UseControllerTracing()` records MVC actions and publishes their chain in the request context. `AttachControllerTraceMetadata()` recovers the completed controller chain from that context for normal MVC results and exception-handler responses after asynchronous filters unwind, even when the ambient chain is no longer visible. These integrations require no additional consumer registration for request-context recovery. `ServiceName` identifies the local chain's owner, such as a Dapr app ID. `UseActorInvocationTracing()` separately enables actor-invocation middleware on a Web host. Chain capture limits default to `MaxChainDepth = 50` and `MaxNodeCount = 1000`.

`UseExecutionTracing()` traces every execution-pipeline operation whose descriptor has `IsBusinessOperation = true`, including event handlers and jobs returning `Task` (adapted to `ExecutionUnit`). It joins an inherited chain or creates an operation-owned root. The owning execution completes that chain on success, exception, or cancellation; nested executions complete only their own node. Result envelopes use their success status (`Ok` or `Created`); other returned results succeed unless they implement `IExecutionOutcome` with `ShouldRollback = true`. Exceptions and cancellation are recorded and rethrown unchanged. Raw infrastructure polling does not become a business operation merely because it executes SQL.

A background chain is available through `IChainTracing` during the execution. This integration does not persist background chains or export them automatically; HTTP response projection remains owned by `AttachControllerTraceMetadata()`. The chain created inside an async execution does not become ambient in its caller after the execution returns. A caller that explicitly initializes or opens a chain retains ownership of that chain's lifetime.

Each chain root includes `timestamp`, the UTC instant when that chain started, serialized as a `DateTimeOffset` with its offset preserved even when the host uses timezone-free `DateTime` formatting. Completion or repeated attachment does not change this timestamp. Set `ServiceVersion` to the deployed host assembly's informational or product version, preferably using the same source as service discovery, to include the full version and commit hash in `serviceVersion`. It defaults to `null`; null or whitespace values are omitted. The example requires `using System.Reflection;` and uses the host's `Program` assembly. `timestamp` is recorded even without `ServiceName`; `service`, `timestamp`, and `serviceVersion` appear only on each chain's root, including remote roots retained inside a `remote` response, and follow the chain's diagnostic visibility policy.

Select `UseEntityFrameworkCoreTracing()` when request-local EF Core timing is needed. It contributes to EF's native `DbContextOptions` pipeline: standard `AddDbContext`, `AddDbContextFactory`, pooled registrations, and Monica Repository's scoped and independent factory contexts attach tracing automatically. The interceptors belong to the host, carry no scoped dependencies, and are not application-facing services. Repeated selection is idempotent:

```csharp
// Inside the host's Monica registration:
monica.AddChainTracing().UseExecutionTracing().UseEntityFrameworkCoreTracing();

// Inside the host's service registration:
services.AddDbContext<OrdersDbContext>(options => options.UseSqlite(connectionString));
```

For manually constructed options or third-party physical/sharding contexts that bypass EF's DI options pipeline, use the explicit adapter below. The host must have selected `UseEntityFrameworkCoreTracing()`; otherwise the adapter fails with a registration error. It preserves the provider, connection, transactions, and existing interceptors, including repository transaction guards. Combining explicit and automatic attachment adds each tracing interceptor only once. Migration/design-time hosts that omit the integration should also omit this adapter.

```csharp
using Monica.Framework.ChainTracing.Extensions;

optionsBuilder.UseChainTracing(hostServices);
```

Database capture requires an active controller, execution, or explicit `IChainTracing.BeginScope(...)` parent. Work without an active parent is skipped, including outbox/inbox polling outside a business execution. The interceptors never create a root. Database leaves do not move the ambient business scope. Callbacks complete the originally captured node; reader leaves attach to the captured business parent even when the callback's async context differs. Once that parent or chain ends, no new reader leaf is started. Capture limits skip additional nodes without affecting execution.

Each node exposes numeric `startOffsetMs` and `durationMs`, measured with a monotonic clock, in addition to the display string `duration`. Offsets refer to the local chain root; remote roots have their own clocks. Database leaves carry a `database` object:

| Field | Meaning |
| --- | --- |
| `phase` | `connection.open`, `command.execute`, or `reader.consume`. |
| `contextType`, `connectionId` | EF context type and logical connection identity. The ID does not identify a physical connection or pool lease. |
| `commandId` | One execution's command ID; null for a connection phase or a group containing multiple executions. |
| `executionCount`, `pendingCount` | Completed executions and starts without a terminal observation. A nonzero pending count means incomplete evidence. |
| `totalDurationMs`, `maxDurationMs` | Sum and maximum of completed execution durations. Gaps between repetitions are excluded. |
| `windowDurationMs` | Window from the first captured start to the latest completion, including gaps or overlaps. The node's `durationMs` is null while any execution is pending. |
| `poolWaitMs`, `physicalConnectMs` | Null: portable EF interception cannot separate these request-local subphases. Null means unobserved, not zero. |

`connection.open` measures EF's opening boundary through completion or failure, including whatever pool acquisition, transport, and authentication the provider performs. An already-open connection produces no opening phase; direct ADO.NET opens outside EF are not intercepted. `command.execute` measures EF's execution boundary through the returned scalar, affected-row count, or reader; this is not database-server-only time. Both use EF's supplied elapsed duration for per-execution statistics. `reader.consume` measures from the returned reader until EF begins closing/disposal, including network reads, materialization, and consumer pauses; it excludes the close itself. Its result is `Closed`, which does not assert complete enumeration or successful materialization. Missing closing observations remain pending, and exceptions during enumeration remain visible on the owning business node. These phases do not add transaction, retry, eager enumeration, connection-opening, or buffering behavior.

Repeated observations aggregate only within the same parent, phase, EF context type, logical connection, and full command shape (command type plus SQL). Parameter values do not split groups and are not captured; command timeout and parameter count are captured. Each completion beyond the first increments `repeatCount`. Any failure/cancellation leaves the group failed and preserves its first exception, even if later completions succeed. `totalDurationMs` can exceed the wall-time window for overlapping operations, so do not add all nodes or subtract cumulative totals from request duration to infer an unexplained wait. Uncompleted command, connection, and reader handles use weak ownership so abandoned provider objects do not retain finished chains indefinitely.

The former `UseDatabaseTracing()` and public concrete command interceptor are removed. Migrate the host to `UseEntityFrameworkCoreTracing()`, remove ordinary manual `AddInterceptors(...)` calls for that interceptor, and use `UseChainTracing(...)` only at options-construction boundaries outside EF DI.

For missing SQL nodes, check an active entry scope, capture limits, and whether a third-party options builder needs the explicit adapter. For missing remote nodes, inspect local trace IDs and the owning service. `tests/Test.Monica.Framework/ChainTracing/ChainTracingEfOptionsTests.cs` covers automatic/factory/pool/manual attachment, host isolation, and incomplete readers. `ChainTracingDatabaseExecutionTests.cs`, `ChainTracingDbCommandInterceptorTests.cs`, and `ChainTracingDbConnectionInterceptorTests.cs` cover async composition, background roots, aggregation, and terminal outcomes.

The trace identifier remains public. Full `metadata.chain`, including SQL text, phase timing, and exception references, is attached only when `ModuleResultEnvelopeOption.ExposeDiagnosticDetails` is enabled; its default is `false`. Each local response owns one top-level chain. Failed nodes use `exceptionId` to reference the response's `metadata.diagnostics.exceptions` catalog instead of repeating exception strings. Capture occurs at final response projection so later propagation frames are retained. Detached node diagnostics in `chain_error` use the same local catalog. Failure summaries are short and do not embed remote JSON. For the exception catalog schema, visibility policy, and compatibility with legacy remote hosts, read [the hosting exception-diagnostics reference](../../monica-infra-hosting/references/exception-diagnostics.md).

Use `IChainTracing.MergeRemoteChain(traceId, remoteResult)` or the matching `ChainTracingScope` method to associate a returned remote result with its local invocation. The node records available remote service/correlation information and a structured `remote` response, including failures represented as result envelopes without a thrown local exception. A directly forwarded result is preserved under that invocation, or under the local root when no invocation is available, before attaching the local chain. Remote chains and catalogs remain inside their `remote` object; their `exceptionId` references resolve within that remote document. Reattaching the same response keeps one local chain rather than appending `chain_1`, `chain_2`, and duplicate stacks.

Chain registration and response ownership are implemented in `Monica.Framework/Modules/ModuleChainTracing.cs`, `Monica.Framework/ChainTracing/Models/ChainTraceNode.cs`, and `Monica.Framework/ChainTracing/Services/Support/ChainResultMetadataAttacher.cs`. Check `tests/Test.Monica.Framework/ChainTracing/ChainResultMetadataAttacherTests.cs` for merge and attachment behavior.
