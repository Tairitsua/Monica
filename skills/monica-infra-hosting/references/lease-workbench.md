# Lease workbench

Reference `Monica.Locker.UI` and call `AddLockerUI()` for `/leases` and `/leases/locks`. The first page shows configured authority observations, holder records, filters, and an exact-resource evidence drawer. The lock page compares the supported [critical-section lock contracts](lockers.md); it does not discover the installed provider or claim to show live lock holders. No lock inventory or forced-unlock API exists.

The UI requires explicit bindings through `AddLeaseDiagnostics`. It never selects a consumer's lease store, starts a coordinator, creates a table, or requires `AddLocker()`. Read [coordinator leases](leases.md) for provider registration, migrations and authority identity before exposing one.

## Bind the existing authorities

`LeaseAuthority` identifies a keyed singleton `IExclusiveLeaseStore`, a stable scope and an ordinal resource-name prefix. IDs must be unique within the host. Bind only low-cardinality coordination roles and avoid overlapping queries against the same store. The display kind (`Sql`, `Redis`, `InMemory`, `Custom`) describes the configured implementation; it neither selects a provider nor grants administrative capabilities.

```csharp
using Monica.Locker.Leases.Diagnostics;
using Monica.Modules;

// The host has already registered "background" as a keyed singleton
// IExclusiveLeaseStore, selected it for Outbox maintenance, and configured its storage.
builder.AddMonica(monica =>
{
    monica.AddLeaseDiagnostics(options => options.Authorities.Add(new LeaseAuthority(
        id: "orders-maintenance",
        displayName: "Orders background coordination",
        storeKey: "background",
        scope: "orders-database",
        namePrefix: "outbox:",
        kind: LeaseAuthorityKind.Redis,
        allowRevocation: false)));
    monica.AddLockerUI(options =>
    {
        options.AuthorizationPolicyOverride = "Operations";
        options.AutoRefreshSeconds = 30;
    });
});
```

`Operations` is an application-owned authorization policy registered by the host. The workbench uses the [shell operational access policy](../../monica-infra-ui/references/shell-and-access.md): outside Development, access fails closed unless a policy is configured (or the host explicitly enables the shell debug bypass). The facade checks current access before every inventory read, exact read and revocation, including cache hits. `LeaseDiagnosticsFacade` is scoped; its provider-reading service and inventory cache are host-owned singletons. Registering `AddLeaseDiagnostics` without the UI requires a caller-scoped `ILeaseDiagnosticsAccess` implementation; its default denies all operations. No HTTP endpoints are added automatically.

An existing scheduler authority can be exposed with a keyed alias to `IJobSchedulerStore.CoordinatorLeases`. Register that alias explicitly and use the scheduler's actual scope and resource prefix. Do not create an independent memory store merely to populate the UI. A wrapper implementing only `IExclusiveLeaseStore` remains read-only even when its underlying database provider supports administration.

## Refresh and source evidence

| Option | Default | Behavior |
| --- | --- | --- |
| `ModuleLockerUIOption.AutoRefreshSeconds` | 30 | Initial circuit preference: 0 (Off), 15, 30 or 60; unsupported values use 30 |
| `ModuleLeaseDiagnosticsOption.InventoryCacheDuration` | 15 seconds | Positive minimum interval between inventory attempts, including failures and manual refreshes |
| `ModuleLeaseDiagnosticsOption.QueryTimeout` | 10 seconds | Positive provider cancellation deadline for a read; a provider must still finish its actual task before admission is released |
| `ModuleLeaseDiagnosticsOption.MaxRecordsPerAuthority` | 500 | Presentation/cache limit, 1–10000; not a limit on the provider's enumeration |

Concurrent viewers in one host share a single inventory read per authority. The cache is per process: separate UI hosts may each read the backend. Redis inventory still performs database-wide `SCAN`; the shared cache reduces request frequency but does not turn it into an indexed lookup. Avoid high-cardinality prefixes and per-message/execution claims. Sources are read sequentially to avoid an unbounded fan-out. Opening or refreshing one exact resource bypasses inventory enumeration and its cache; providers must implement the exact lookup directly.

Automatic refresh pauses while the document is hidden. The route serializes admitted work, resets the delay after each completed refresh, and drains actual work on disposal; no overlapping timer requests accumulate. The interval preference survives navigation within the circuit. Browser reload restores the configured initial preference. Inventory refresh also refreshes an open resource's exact evidence.

Each source retains its own `ObservedAtUtc`; sources do not form an atomic distributed snapshot. A failed inventory refresh retains that source's last successful records and original time, visibly marked stale. An initial failure is unavailable, not an empty authoritative result. Expiry is evaluated against the source observation time, never the viewer's clock; values are snapshots rather than live countdowns. Expired SQL/memory rows may remain while expired Redis keys disappear. Missing records are not ownership history. Visible timestamps use the host's [time display policy](../../monica-infra-ui/references/time-display.md); machine-readable `datetime` values preserve the original instant.

## Operator revocation

Revocation is offered only when the binding opts in with `allowRevocation: true` **and** the resolved store implements `ILeaseAdministration`. The action captures the resource identity for confirmation, requires acknowledgement of possible overlapping work, and rechecks authorization at execution. It revokes whichever grant exists at that moment, which may differ from the displayed holder. A missing record is a successful no-op. Revocation is serialized against inventory adoption and invalidates the affected cache before readback, preventing a pre-mutation scan from repopulating it afterward.

Revocation does not interrupt work, fence external writes or guarantee immediate failover. Old work learns of loss at renewal/deadline. The current action and outcome are logged with authority and resource; the host owns operator identity/audit integration. Preserve transactional guards and idempotency in consumers. Do not expose revocation as normal worker release or advertise it as a safe stop button.

Behavior checks live in `tests/Test.Monica.Locker/Leases/LeaseDiagnosticsTests.cs`: shared concurrent reads, exact-read independence, source-clock retention, failure throttling, authorization, query containment, cancellation drain and revocation/read races. Use the real-host browser workflow for UI checks; screenshots and fixtures are not evidence of production authority health.
