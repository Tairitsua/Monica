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

`UseControllerTracing()` records MVC actions and publishes their chain in the request context. `AttachControllerTraceMetadata()` recovers the completed controller chain from that context for normal MVC results and exception-handler responses after asynchronous filters unwind, even when the ambient chain is no longer visible. `UseExecutionTracing()` traces business operations returning `IResultEnvelope`. These integrations require no additional consumer registration for request-context recovery. `ServiceName` identifies the local chain's owner, such as a Dapr app ID. `UseActorInvocationTracing()` separately enables actor-invocation middleware on a Web host. Chain capture limits default to `MaxChainDepth = 50` and `MaxNodeCount = 1000`.

Each chain root includes `timestamp`, the UTC instant when that chain started, serialized as a `DateTimeOffset` with its offset preserved even when the host uses timezone-free `DateTime` formatting. Completion or repeated attachment does not change this timestamp. Set `ServiceVersion` to the deployed host assembly's informational or product version, preferably using the same source as service discovery, to include the full version and commit hash in `serviceVersion`. It defaults to `null`; null or whitespace values are omitted. The example requires `using System.Reflection;` and uses the host's `Program` assembly. `timestamp` is recorded even without `ServiceName`; `service`, `timestamp`, and `serviceVersion` appear only on each chain's root, including remote roots retained inside a `remote` response, and follow the chain's diagnostic visibility policy.

Use `UseDatabaseTracing()` only when EF Core command nodes are needed. It registers `ChainTracingDbCommandInterceptor` in DI; the application's DbContext configuration must also attach that interceptor. Controller, execution, database, and RPC integrations are separate choices, so a chain with no SQL nodes may have an unattached interceptor. Inspect local trace IDs and the owning service before diagnosing missing remote nodes.

The trace identifier remains public. Full `metadata.chain`, including SQL text, parameter values, and exception references, is attached only when `ModuleResultEnvelopeOption.ExposeDiagnosticDetails` is enabled; its default is `false`. Each local response owns one top-level chain. Failed nodes use `exceptionId` to reference the response's `metadata.diagnostics.exceptions` catalog instead of repeating exception strings. Capture occurs at final response projection so later propagation frames are retained. Detached node diagnostics in `chain_error` use the same local catalog. Failure summaries are short and do not embed remote JSON. For the exception catalog schema, visibility policy, and compatibility with legacy remote hosts, read [the hosting exception-diagnostics reference](../../monica-infra-hosting/references/exception-diagnostics.md).

Use `IChainTracing.MergeRemoteChain(traceId, remoteResult)` or the matching `ChainTracingScope` method to associate a returned remote result with its local invocation. The node records available remote service/correlation information and a structured `remote` response, including failures represented as result envelopes without a thrown local exception. A directly forwarded result is preserved under that invocation, or under the local root when no invocation is available, before attaching the local chain. Remote chains and catalogs remain inside their `remote` object; their `exceptionId` references resolve within that remote document. Reattaching the same response keeps one local chain rather than appending `chain_1`, `chain_2`, and duplicate stacks.

Chain registration and response ownership are implemented in `Monica.Framework/Modules/ModuleChainTracing.cs`, `Monica.Framework/ChainTracing/Models/ChainTraceNode.cs`, and `Monica.Framework/ChainTracing/Services/Support/ChainResultMetadataAttacher.cs`. Check `tests/Test.Monica.Framework/ChainTracing/ChainResultMetadataAttacherTests.cs` for merge and attachment behavior.
