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

Use `UseDatabaseTracing()` only when EF Core command nodes are needed. It registers a scoped `ChainTracingDbCommandInterceptor` in DI; the application's DbContext configuration must also attach that interceptor:

```csharp
// Inside the host's Monica registration:
monica.AddChainTracing().UseExecutionTracing().UseDatabaseTracing();

// Inside the host's service registration:
services.AddDbContext<OrdersDbContext>((provider, options) => options
    .UseSqlite(connectionString)
    .AddInterceptors(provider.GetRequiredService<ChainTracingDbCommandInterceptor>()));
```

SQL capture requires an active controller, execution, or explicit `IChainTracing.BeginScope(...)` parent. A command without an active parent is skipped, including outbox/inbox polling outside a business execution. The interceptor never creates a root. Completion uses the node captured at command start, so EF's asynchronous composition of multiple interceptors cannot detach it from its chain. Preserve other interceptors, including repository transaction guards. Database leaves do not move the ambient business scope.

Repeated commands with identical command type and full SQL text aggregate within the same parent node; parameter values do not split that group. Each completed execution after the first increments `RepeatCount`, extends the node's duration window, and updates its result summary. Any failed execution leaves the group failed and preserves its first exception, even when completions arrive out of order. Other parent scopes and later chains keep separate nodes, and a reused interceptor does not retain completed chains through its aggregation cache. Capture limits skip additional nodes without affecting SQL execution.

For missing SQL nodes, check both interceptor attachment and an active entry scope; database tracing registration alone is insufficient. For missing remote nodes, inspect local trace IDs and the owning service. The async composition and background-root contracts are covered by `tests/Test.Monica.Framework/ChainTracing/ChainTracingDatabaseExecutionTests.cs`; aggregation and completion ownership are covered by `ChainTracingDbCommandInterceptorTests.cs` in the same directory.

The trace identifier remains public. Full `metadata.chain`, including SQL text, parameter values, and exception references, is attached only when `ModuleResultEnvelopeOption.ExposeDiagnosticDetails` is enabled; its default is `false`. Each local response owns one top-level chain. Failed nodes use `exceptionId` to reference the response's `metadata.diagnostics.exceptions` catalog instead of repeating exception strings. Capture occurs at final response projection so later propagation frames are retained. Detached node diagnostics in `chain_error` use the same local catalog. Failure summaries are short and do not embed remote JSON. For the exception catalog schema, visibility policy, and compatibility with legacy remote hosts, read [the hosting exception-diagnostics reference](../../monica-infra-hosting/references/exception-diagnostics.md).

Use `IChainTracing.MergeRemoteChain(traceId, remoteResult)` or the matching `ChainTracingScope` method to associate a returned remote result with its local invocation. The node records available remote service/correlation information and a structured `remote` response, including failures represented as result envelopes without a thrown local exception. A directly forwarded result is preserved under that invocation, or under the local root when no invocation is available, before attaching the local chain. Remote chains and catalogs remain inside their `remote` object; their `exceptionId` references resolve within that remote document. Reattaching the same response keeps one local chain rather than appending `chain_1`, `chain_2`, and duplicate stacks.

Chain registration and response ownership are implemented in `Monica.Framework/Modules/ModuleChainTracing.cs`, `Monica.Framework/ChainTracing/Models/ChainTraceNode.cs`, and `Monica.Framework/ChainTracing/Services/Support/ChainResultMetadataAttacher.cs`. Check `tests/Test.Monica.Framework/ChainTracing/ChainResultMetadataAttacherTests.cs` for merge and attachment behavior.
