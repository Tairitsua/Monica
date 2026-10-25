# Observable instance evidence

Use `monica.AddObservableInstance()` inside `builder.AddMonica(...)` when an application object needs a host-owned state tracker and bounded diagnostic history. The registry strongly retains registered trackers; it does not automatically unregister application objects when garbage collection runs. Choose unique instance IDs and supply the actual type, optional service key, and group in the registration. `DefaultMaxHistorySize` defaults to 100; a registration can override `MaxHistorySize`. Hosted-service observation owns its reversible registrations separately.

```csharp
var registry = host.Services.GetRequiredService<IObservableInstanceRegistry>();
var tracker = registry.Register("import/customer-feed", registration =>
{
    registration.InstanceName = "Customer feed import";
    registration.GroupId = "Imports";
    registration.MaxHistorySize = 200;
    registration.DefaultLogLevel = LogLevel.Information;
});
tracker.RecordState("Import checkpoint reached", new { Batch = 42, Accepted = 120 });
var captured = tracker.CaptureSnapshot(cancellationToken);
```

`RecordState` records messages and exceptions as well as state changes. Its legacy `TotalStateChanges` property therefore counts lifetime **recorded entries**, and `StateChangedAt` is the last record time, including message-only records. A registration without any records has no meaningful last-record time even though that legacy property is initialized at registration. An explicit entry log level does not override the current state's mapping. Configure enum mappings with `SetInformationStates`, `SetWarningStates`, and related methods, or a default level for unmapped states.

`CaptureSnapshot` freezes tracker-owned metadata, mappings, counters, current state references, and all retained entries under one tracker lock. It then serializes values and exceptions outside that lock into detached text. It retains no arbitrary state or exception objects, does not truncate values, and reports serialization failures through `CaptureError` rather than substituting an invented value. Caller-owned mutable objects need their own synchronization if their serialization must be atomic. Capture cancellation is checked before reading and between entries; custom property getters and exception formatting are synchronous user code and cannot be interrupted mid-call.

The host-resolved `ObservableInstanceFacade.CaptureAsync` takes one registry membership read and captures its trackers independently. It omits a registration retired during the read; it does not promise a globally atomic registry transaction. Each `ObservableInstanceSnapshot.Identity` includes the registry ID and an immutable registration generation. Use the identity overload of `ClearInstanceHistoryAsync` for commands derived from a captured view; it rejects a replacement registration with the same ID. The existing string-ID overload targets the current registration by ID for compatibility. Clearing removes only retained history and its exception evidence. Current state, registration, lifetime totals, and monotonic record numbering are preserved. Retention eviction similarly removes evidence without reducing lifetime counters.

`monica.AddObservableInstanceUI()` contributes the registry overview at `/observable-instance-monitor`, the registration ledger at `/observable-instance-monitor/instances`, the group browser at `/observable-instance-monitor/groups`, and the retained exception ledger at `/observable-instance-monitor/exceptions`. They use normal shell registration and access checks. Within one Blazor circuit, moving among these routes keeps the captured evidence and filters; manual refresh obtains new evidence. Leaving the workspace releases that read. There is no background polling or historical archive. Inspectors freeze the selected registration's complete retained evidence, including source-qualified entry sequences. Every retained entry remains reachable through local pagination.

The UI distinguishes current mapped health from retained exception evidence and lifetime occurrences. Warning, Error, and Critical are current concerns; Trace, Debug, and Information are mapped healthy states; an absent level or None remains unknown. A recovered registration can still have retained exceptions. An empty retained exception ledger does not prove that no exceptions ever occurred. The registry is a diagnostic view and does not replace a deployment readiness probe.

Exception inspectors retain the raw capture disclosure and an optional formatted reading surface. The shared diagnostic-text viewer in `$monica-infra-ui` (`references/stack-trace.md`) preserves complete encoded source, offers wrapping and exact-payload Copy, and leaves vertical scrolling to the inspector. Formatting does not infer a cause tree or replace frozen capture provenance.

For verification, use `tests/Test.Monica.Core/ObservableInstance/ObservableSnapshotTests.cs` for detached values, coherent concurrent reads, cancellation, retention/clear semantics, replacement registration rejection, and host registry isolation. UI verification uses a real bridge host with registered trackers rather than injected presentation DTOs.
