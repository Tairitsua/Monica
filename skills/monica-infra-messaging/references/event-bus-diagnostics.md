# Inspect EventBus diagnostics

Capture, notification and test-result timestamps follow the shell's page time display policy in `$monica-infra-ui` (`references/time-display.md`). Runtime instants and retained diagnostic payloads remain exact. Provider implementation identity stays available in the heading tooltip and detail view; the directory states its count semantics once.

Subscription test payloads use the shared JSON editor and viewer in `$monica-infra-ui` (`references/json.md`). Syntax feedback and formatting leave test-command admission, service validation and receipt evidence with the EventBus UI.

`monica.AddEventBusUI()` composes the EventBus monitor, provider discovery, scoped capture state and diagnostic test service, localization, and shell navigation. Local diagnostics and targeted local delivery require no distributed provider. Configure a distributed provider only when the host needs external publishing or receipt. The workbench has five independently addressable routes:

| Route | Evidence or operations |
| --- | --- |
| `/event-bus-monitor` | Overview of captured registration and topic-runtime evidence. |
| `/event-bus-monitor/providers` | Configured buses, exact keys/scopes, and optional protected option inspection. |
| `/event-bus-monitor/subscriptions` | Registered-handler filtering, frozen inspection, and exact-ID lifecycle actions. |
| `/event-bus-monitor/changes` | The circuit's bounded observed registry changes and notification-loss evidence. |
| `/event-bus-monitor/test-tools` | Standalone and selected-subscription diagnostics using real bus boundaries. |

```csharp
builder.AddMonica(monica =>
{
    monica.AddEventBusUI(options => options.TestListenerMessageLimit = 20);
});
```

This is a module-composition fragment. The consuming ASP.NET Core host still owns its UI setup in `$monica-infra-ui`, authentication, authorization, `app.UseMonica()`, and `app.MapMonica()` lifecycle.

## Separate registered handlers from transport health

The subscription registry is host-wide across local, default distributed, and keyed buses. Identify an operational target by `EventSubscriptionId`; event and handler labels are for display and do not replace that ID. Registration state and external topic runtime state describe different boundaries:

| Observation | Meaning |
| --- | --- |
| `Active` registration | The handler is registered to receive; it does not prove that its external transport is working. |
| `Inactive` registration | The handler is paused and may be reactivated. |
| `Pending` registration | The handler has not committed activation yet. Ordinary `SubscribeAsync` creates and activates together. |
| `Disposed` registration | The handler resources have been released. Ordinary unsubscription removes it from the current registry; a removal notification can still show its detached identity. |
| `Held` registration | Startup policy holds automatic external receipt. Explicit local delivery may run the handler without activating it. |
| `Disabled` registration | Placement excludes the handler. Neither activation nor local delivery can run it. |
| `Healthy` topic | The provider reports an established, stable external subscription. A healthy but idle topic can have an old or absent last-message time. |
| `Subscribing` topic | External subscription establishment is in progress. |
| `Recovering` topic | Reconnection is being retried after failures. |
| `Failed` topic | The supervisor or consumer loop has terminated without retrying; a registration-state activation is not a transport restart command. |
| `Stopped` topic | The external subscription was removed cleanly. |
| No topic snapshot | Local subscriptions have no external topic state; distributed providers may not have reported one yet. This is not a healthy status. |

Runtime snapshots join distributed subscriptions by the exact `(nullable ServiceKey, TopicName)` pair. Several handlers on one topic can therefore share one runtime status and its message counters. `UnhealthySubscriptions` counts active registered handlers with recovering or failed topics; `UnhealthyTopics` counts recovering or failed topic entries in the status store. Those numbers have different scopes. Provider subscription totals use its exact nullable key and local/distributed scope and exclude hidden row-level test listeners.

`ProcessedMessages` and `LastMessageReceivedAt` are sampled provider counters. Successful message processing does not emit a status-change notification, so refresh snapshots to obtain current values. Message-level errors do emit notifications but do not themselves change topic runtime state. Last-error details survive a recovery to healthy and must not be treated as evidence of current failure. The store retains the most recent 10 errors in chronological order; counts are cumulative for this host process, not a durable broker history.

## Filter and inspect exact identities

`EventBusMonitorService.FilterSubscriptionsAsync(filter)` queries the registry and maps clean type names and runtime status before filtering. `EventBusMonitorService.ApplyFilter(capturedSubscriptions, filter)` applies the same contract to an already captured view:

- Registration state, topic runtime state, scope, discovery mode, service key, and selected provider combine conjunctively.
- A selected provider matches its exact nullable key and local/distributed scope. The default null key is different from a named key spelled `default`; comparisons remain ordinal and case-sensitive.
- `ServiceKey == null` leaves the standalone key filter unset. Any non-null value matches exactly, including an empty or whitespace key.
- Text search is case-insensitive over clean full and compact event/handler names, topic, service key, handler factory, action metadata, and subscription ID. A complete GUID matches in either the ordinary hyphenated or canonical compact form.
- A concrete runtime-state filter excludes local and unreported distributed rows. An active-registration filter can still include a failed external topic.

`EventBusProviderInfo.UniqueId` is an opaque selection identifier that distinguishes provider kind, bus scope, null keys, and exact named keys. Do not parse it or replace the key with a localized default-provider label. Display `ServiceKey` separately and preserve it unchanged when resolving a bus.

Provider discovery inspects local bus registrations and distributed bus registrations with a matching `IEventTransport`, without resolving gateways, transports, producers or external clients. The no-op provider has no transport and is not reported as an external transport. Unambiguous provider-module metadata supplies kind, capability and declared-module hints; absent or ambiguous metadata remains `Unknown`. Discovery does not establish which factory implementation will be constructed or prove connection health. Explicit route publication and listener operations resolve their providers when invoked.

## Capture current evidence and freeze inspection

`EventBusWorkbenchState` is borrowed scoped state. Its `RefreshAsync(cancellationToken)` serializes refreshes and replaces `Snapshot` only after subscriptions, discovered providers, and topic statuses have all been read successfully. A failed refresh preserves the last successful capture and records `RefreshError`; cancellation propagates without adopting a late result or replacing the previous error. `CapturedAt` advances only with an accepted complete capture. Before the first successful capture, `Snapshot`, `Statistics`, and `CapturedAt` are null; `HasAttemptedCapture` distinguishes an attempted failure from a view that has not loaded yet.

Read `Snapshot` once when comparing its lists and aggregates. Handler statistics and each provider's exact nullable-key/scope counts derive from that capture's same visible subscription set. Unhealthy-topic counts retain the separately sampled full status-store scope. Distributed rows join runtime status from the captured `(ServiceKey, TopicName)` pair; missing runtime evidence stays null. The reads are sequential observations of running services, not a transaction that freezes the registry or transport.

Capture membership and topic-error collections are immutable, and metadata is detached from registry and feed producers. Existing mutable view-model types remain compatible and are read-only by contract inside a capture. Use `EventBusWorkbenchState.CloneSubscription(row)` for an independently frozen inspector; it copies metadata and topic error history. Retain the inspector's freeze time separately from the source capture time. Neither later feed notifications nor refreshes update the frozen row.

Components own their cancellation sources, feeds, reader tasks, refresh scheduling, observation generations, and dialogs; they never dispose the borrowed state. The state creates no timers, callbacks, or background tasks. A mounted view calls `BeginObservation()`, passes that generation and its lifetime token to `RecordChange(...)`, and calls `EndObservation(generation)` when it retires. New generations reject updates from previous readers, and an older owner cannot retire a newer generation. `ApplyFilter(filter)` uses the shared filter over the current capture without another registry query.

## Own each monitor change feed

`EventBusMonitorService` is a host-owned singleton. A component borrows it and owns only its feed and reader loop:

```csharp
using var feed = monitor.OpenChangeFeed(viewLifetimeToken);
await foreach (var change in feed.Reader.ReadAllAsync(viewLifetimeToken))
{
    // Refresh current registry/runtime snapshots; treat the notification payload as read-only.
    // SubscriptionChanged can also append to this view's bounded observed-changes list.
}
```

Every feed receives an independent broadcast buffer. Observation starts lazily, and a newly opened feed does not replay earlier changes. Each feed retains at most 256 unread notifications; a full buffer drops its oldest notification and increments that feed's cumulative `DroppedChangeCount`. A nonzero value requires a current-snapshot refresh and an explicit indication that the view's observed change list is incomplete. Even without loss, this list represents changes observed during this view's lifetime, not all host history, delivery acknowledgments, or persisted audit records. Topic-status notifications invalidate runtime views but do not create subscription-lifecycle history entries.

Workbench state retains the newest 100 accepted detached registry changes across mounted views in the same circuit. `ObservedChangeCount` includes accepted entries beyond that display window; `DroppedChangeCount` accumulates reported notification loss across accepted feed generations. Window truncation is distinct from feed loss, repeated reports of one feed's cumulative counter are not counted twice, and changes while no view was observing are not reconstructed. Pass the feed's current loss counter to `RecordChange` even for topic-only invalidations. Notifications never mutate a successful capture; refresh it separately, with coalescing or periodic/manual reads as the component requires.

Dispose the feed when the component retires. Cancellation supplied to `OpenChangeFeed` also retires its lease; already buffered notifications remain drainable. The component must cancel and await its own reader task before releasing its cancellation source. It must not dispose the singleton monitor. Host shutdown retires all feeds and source observers, and repeated feed or host disposal is harmless.

The reader-only adapter `SubscribeToChanges(cancellationToken)` requires a cancellable consumer-lifetime token and retires its independent lease on cancellation. The former parameterless shared-reader pattern is unsupported: it could distribute changes between unrelated views and had no consumer-owned retirement boundary. Prefer `OpenChangeFeed` when the UI needs notification-loss evidence.

## Distinguish route publication from local delivery

Activation, deactivation, and removal target the exact current subscription ID. An inactive handler with `Automatic` activation policy may be reactivated; held, disabled and disposed handlers cannot be activated. Deactivation requires an active registration. Unsubscription releases the handler factory and removes the registry entry. These actions change real host handlers; they do not act only on the currently filtered table. Stop any row-level listener owned by the current circuit before removing its source subscription.

`EventBusTestService.PublishRuntimePayloadAsync(id, json, cancellationToken)` validates and deserializes JSON using the exact selected event type, then prepares a real message with that subscription's local/distributed scope, exact service key, and topic. It sends immediately through the local receive dispatcher or the exact keyed/default distributed transport, including for Outbox-marked contracts. This diagnostic bypasses Outbox staging and requires no publishing transaction; ordinary application publication retains the [outbox/inbox contract](../../monica-infra-persistence/references/transactional-events.md). Every matching handler can receive the event, not just the selected row. Local success means dispatch completed; distributed success means transport acceptance, without confirming consumer completion or durable delivery. A listener receipt remains separate evidence.

The row dialog's separate **Deliver to this handler locally** action uses `PrepareLocalTestMessage(id, json)` followed by `DeliverLocalTestMessageAsync(id, message, cancellationToken)`. Preparation uses the selected contract, topic, scope and key but invokes no handler or transport. Delivery calls native `IEventReceiveDispatcher.DispatchTestAsync` with that one exact registry ID. Only an `Active` or `Held` target is eligible, and the current registry state is revalidated at execution. The normal scoped handler, transaction and Inbox pipeline runs with real effects; no broker publish, transport acknowledgement, listener creation or activation occurs. A held target stays held, and other matching handlers do not run.

Every new delivery gets a fresh message ID. The dialog retains only its most recently prepared message, including after a failed handler attempt. Explicit **Replay** reuses that message's original ID, source and unchanged body; the payload editor is disabled while replay is selected. Inbox may skip a previously processed message, so pipeline completion does not prove the handler body ran again. Closing the dialog cancels and drains its admitted command and refresh loop. Cancellation is cooperative with the handler pipeline and does not undo effects already committed or release resources before the command finishes.

`CreateRuntimeJsonSample(id)` returns JSON through the host's serializer options. DateOnly and TimeOnly values, including nullable nested values, use actual sample values and configured converters rather than object-shaped placeholders. Validate the generated sample before sending it. `tests/Test.Monica.Framework.UI/EventBus/EventBusTestServiceTests.cs` covers default/keyed local and distributed immediate sends, Outbox bypass, cancellation/failure projection and date/time converters through real host composition without UI rendering.

Row-level `StartRuntimeListenerAsync(id)` adds a real extra listener for the selected type, topic, key, and scope only when the source's activation policy is `Automatic`. It cannot turn a held or disabled target into automatic receipt. Its metadata marks it hidden from ordinary subscription lists, subscription totals, provider subscription counts, and registry-change feeds. The listener captures only messages actually delivered to it. `TestListenerMessageLimit` defaults to 20 per active listener and clamps values below one to one; oldest retained messages are discarded as new ones arrive. Listener messages are an in-memory sample, not complete delivery history. The scoped service serializes row starts, stops, and cleanup through one ownership gate: overlapping starts reuse one real registry handle, and a start behind a successful stop creates a new handle only after the old one is removed. A failed stop preserves the exact handle and retained messages for retry. Scope retirement rejects queued or new row starts, drains an admitted registry start through handle adoption, then attempts exact cleanup; repeated disposal can retry a failed removal. Row start and stop APIs have no cancellation argument, so components must drain their admitted operations rather than abandon them.

The scoped test service owns these listeners. Closing the row test dialog retires its 500 ms refresh loop while an explicitly started listener continues until stopped, source removal by that circuit, or scope disposal. The standalone test panel differs: it uses the default distributed bus and `TestEventMessage` and retains at most 100 messages. Its component acquires `AcquireStandaloneListenerLeaseAsync(lifetimeToken)`, which waits for the prior mount to release exclusive control, then reads the lease's immutable `Snapshot` to adopt an exact retained handle/topic. Lease `SubscribeAsync(topic)` and `UnsubscribeAsync()` serialize listener operations. A failed stop retains that handle for retry and blocks replacement starts. Retirement cancels/drains a pending acquisition, drains admitted commands, attempts stop, and always disposes the lease. Lease disposal releases control after in-flight service operations finish without discarding a failed-stop handle; the next lease can recover it, and a retired lease cannot stop a later owner's listener.

The legacy unleased `SubscribeToTestTopicAsync` and `UnsubscribeFromTestTopicAsync` remain available for callers without a component lease; they cannot interrupt an active lease. Scoped-service disposal rejects new starts/leases, cancels ownership waiters, and attempts exact-handle cleanup regardless of the current lease, permitting repeated disposal to retry failed removal. Standalone start/stop operations have no cancellation argument, so the component must await admitted operations before releasing its borrowed scope. Standalone publication does not require a listener lease. The no-op distributed composition provider can accept a publish without external delivery; actual receipt must be established by listener evidence.

## Preserve access and configuration boundaries

The host owns protection of all five workbench routes and optional `/eventbus-ui/*` Minimal API endpoints. `ModuleEventBusUIOption.EnableMinimalApi` follows the module-system default when unset. `ModuleEventBusUI` requires `ModuleResultEnvelope`, which transitively supplies JSON serialization and registers the error-message provider used by these endpoints' `Res.GetResponse()` projection; callers do not need an extra `AddResultEnvelope()` registration. The module does not impose an authorization policy on its subscription read or lifecycle endpoints; Monica endpoint-port metadata is not an authentication policy. Apply the consuming host's intended authorization to both direct endpoints and the UI.

Provider configuration is a separate protected diagnostics boundary. Its dialog loads module-option diagnostics only when the configuration tab opens and `OperationalPageAccessEvaluator` permits access. Development or explicitly enabled operational debug mode allows it; production otherwise needs a resolvable, satisfied operational policy. `ModuleSystem` diagnostics remains optional: the dialog resolves `ModuleDiagnosticsFacade` only after access is permitted, and a host without that facade shows unavailable option inspection. Do not turn this optional inspection into a new mandatory EventBus dependency. Tab changes, provider changes, dialog closure, and disposal clear loaded options and reject late authorization results. Keep the diagnostics facade's redaction/reveal contract, and reauthorize on return to the configuration tab.

## Evidence and behavioral checks

The owning sources are `Monica.Framework.UI/Modules/ModuleEventBusUI.cs`, `UIEventBus/State/EventBusMonitorService.cs`, `State/EventBusWorkbenchState.cs`, `State/EventBusTestService.cs`, `Support/EventBusProviderDiscoveryService.cs`, `Models/EventBusProviderInfo.cs`, `Models/EventBusWorkbenchSnapshot.cs`, `Models/SubscriptionFilter.cs`, `Models/SubscriptionViewModel.cs`, and `Dialogs/EventBusProviderDetailDialog.razor`; registration and runtime semantics belong to `Monica.EventBus/Services/EventSubscriptionRegistry.cs` and `Services/TopicSubscriptionStatusStore.cs`.

Non-rendering contract checks under `tests/Test.Monica.EventBus/UIEventBus/State/` and `Models/` cover independent fan-out, cancellation/retirement, bounded loss, exact provider and filter identity, combined filtering, unknown runtime state, and sampled counters. Host-owned state/service scenarios also exercise scoped isolation, coherent capture counts, cancelled or failed refresh adoption, detached inspection/history, and exact-handle standalone stop failure/retry. Browser verification must additionally exercise two simultaneous views, real subscription lifecycle controls, listener persistence and cleanup, actual receipt through the selected bus, denied provider-options access, and table/dialog responsive containment. Do not claim a transport integration or UI behavior from the service tests alone.
