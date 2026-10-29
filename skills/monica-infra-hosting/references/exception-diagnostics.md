# Exception response diagnostics

`AddExceptionHandling()` requires a Web host and converts unhandled failures into Monica result envelopes. Configure diagnostic visibility on `AddResultEnvelope(...)`; `ExposeDiagnosticDetails` defaults to `false` and does not follow the ASP.NET Core environment automatically. For a host whose consumers may inspect technical details, add these registrations inside the existing `builder.AddMonica(...)` callback:

```csharp
monica.AddExceptionHandling();
monica.AddResultEnvelope(options => options.ExposeDiagnosticDetails = true);
```

`BusinessException` represents a permanent business-rule rejection, not a transient dependency failure. The HTTP exception handler projects it as `Res.Fail(business.Message)`. Distributed EventBus consumers use its type to end a failed delivery; follow the [EventBus failure contract](../../monica-infra-messaging/references/event-bus.md#rejected-deliveries-and-retries) before choosing it in a handler. Response diagnostic visibility does not change delivery or retry decisions.

With the switch enabled, an exception response contains `metadata.diagnostics`, an `ExceptionDiagnosticDocument` with `schemaVersion: 1`, a root `exceptionId`, optional request context, and an `exceptions` catalog. Each entry has an `id`, CLR `type`, its own `message`, its own `stackTrace` array, and optional `innerExceptionIds`. Runtime exceptions are captured once by object identity at response projection: the same propagated exception and shared causes use the same ID, while distinct exceptions with identical text remain distinct. Aggregate exceptions retain their direct causes. This avoids copying `Exception.ToString()` into each boundary or chain node; repeated frames within one actual stack remain meaningful evidence and are retained.

For a failed local transaction, `TransactionRollbackOnlyException` identifies the rejected action and the first failure's operation, reason, and correlations. Its inner exception is the original failure even when application code caught it earlier; the catalog preserves that original stack by identity. Result-only rollback causes have a status and message but no artificial inner exception. The scoped failure snapshot and structured transaction log are described in [first-failure diagnostics](../../monica-infra-persistence/references/transactions.md#first-failure-diagnostics).

The following diagnostic metadata fragment shows a wrapper and its cause. Call-chain nodes, when enabled, refer to the same catalog through `exceptionId`:

```json
{
  "diagnostics": {
    "schemaVersion": 1,
    "exceptionId": "e1",
    "exceptions": [
      {
        "id": "e1",
        "type": "System.InvalidOperationException",
        "message": "The operation failed.",
        "stackTrace": ["at Example.Service.Execute()"],
        "innerExceptionIds": ["e2"]
      },
      {
        "id": "e2",
        "type": "System.ArgumentException",
        "message": "The supplied value is invalid.",
        "stackTrace": ["at Example.Validator.Validate()"]
      }
    ]
  },
  "chain": { "exceptionId": "e1", "children": [] }
}
```

Remote failures belong to a separate `remote` object with the reported status, transport, service, trace identifier, request information, and its own `diagnostics` and `chain` when available. A transport exception owns this object on its catalog entry; a returned remote result can instead attach it to the corresponding local call-chain node. Every remote document has an independent ID scope, so a remote `e1` never refers to the caller's `e1`. Readers must resolve chain and cause references within their owning document. The receiver normalizes current catalogs and recognized legacy `metadata.exception` / chain `exceptionMessage` forms, including historical additional-chain keys, allowing services to upgrade separately. New consumers should read `metadata.diagnostics` rather than the removed local `metadata.exception` or per-node `exceptionMessage` output. Capture limits bound text, stacks, catalog size, and remote/chain nesting; `truncated` marks omitted detail.

When diagnostics are disabled, presentation removes reserved technical metadata, including `diagnostics`, `exception`, `detail`, `chain`, `chain_error`, and historical numeric variants such as `chain1` and `chain_1`. The public error and correlation contract remains available. The remote-call boundary follows the receiving host's diagnostic switch; enabling it does not make downstream hosts disclose details they did not send. Operator logging continues to receive the full exception object independently of this response switch.

Enabling diagnostics also configures the host's canonical JSON encoder for readable UTF-8 text and CLR punctuation, such as Chinese messages, backticks, and angle brackets in stack frames. This is a host-wide wire-contract setting shared by its JSON consumers, not a postprocessing pass over exception strings. JSON escaping for quotes, backslashes, and control characters remains valid; a literal `\\u0060` string remains literal. Parse JSON once and consume it as JSON rather than embedding raw response text into HTML or script. For chain attachment and merging, read [the observability tracing reference](../../monica-infra-observability/references/tracing.md); for the Dapr transport adapter, read `$monica-infra-messaging`.

The exception response contract is owned by `Monica.Core/Modules/ModuleResultEnvelope.cs`, `Monica.Core/ExceptionHandling/Models/ExceptionDiagnosticDocument.cs`, and `Monica.Core/ExceptionHandling/Services/ExceptionDiagnosticProjection.cs`. Check `tests/Test.Monica.Core/ExceptionHandling/ExceptionDiagnosticProjectionTests.cs` and `ModuleExceptionHandlingIntegrationTests.cs` for behavior.
