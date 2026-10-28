# Inspect Kafka broker evidence

`KafkaConsoleDbContext` marks its cluster and performance snapshot tables for [common infrastructure layout](../../monica-infra-persistence/references/infrastructure-table-layout.md). Configure its context options and migration factory with the same layout and a dedicated history key. Existing table identifiers remain module-owned; selecting an infrastructure schema does not rename tables or move broker configuration data.

Register `monica.AddEventBusKafkaUI()` in an ASP.NET Core host with the Monica shell. The UI requires the Kafka backend and contributes its localization resource and five shell/search pages. Configure cluster access with `AddEventBusKafka().AddConfiguredCluster(cluster)` for console visibility, or `UseKafkaProvider(cluster)` when Kafka also supplies the distributed EventBus transport. Console configuration alone does not select that transport. Direct administration requires broker addresses and Monica-owned credentials; externally managed credentials permit configuration visibility without enabling native broker actions.

When the backend's Minimal API is enabled, its endpoints use Monica's normal result-envelope serialization and message policy. The Kafka backend declares `ModuleResultEnvelope` as a dependency; a host does not need to add it separately to make these responses work. The browser workspace and HTTP API consume the same facade, while the shell's operational-page access policy still controls page access.

The cluster selected by `UseKafkaProvider(...)` is owned by startup configuration. Its identity takes precedence over configured or persisted console records, and console upsert/delete operations reject that identity. Change its connection through the host's startup configuration and restart the host. Broker administration still targets the actual runtime cluster.

Cluster summaries returned to the UI or HTTP API omit SASL usernames and passwords. Connection probes and broker operations resolve the private configuration on the server; redacted summary configuration must never be used as their credential source. When editing a console-managed cluster, leaving either credential empty preserves its stored value, while a nonempty replacement updates it. Passwords retain their exact characters, including whitespace. Persisted cluster records continue to store the real credentials; redaction applies at the presentation boundary. A saved edit changes `UpdatedAt`, invalidating earlier connection evidence even when only credentials changed; ordinary listing retains that revision.

## Routes and broker context

| Route | Task |
| --- | --- |
| `/eventbus-kafka` | Integration identity and available broker evidence. |
| `/eventbus-kafka/workspaces/clusters` | Configure clusters and run connection probes. |
| `/eventbus-kafka/workspaces/topics` | Inspect topology, retention, messages, backlog, and topic administration. |
| `/eventbus-kafka/workspaces/consumer-groups` | Inspect state, members, assignments, and committed offsets. |
| `/eventbus-kafka/workspaces/performance` | Capture and compare offset-based performance evidence. |

The existing entry URL remains the overview. Contextual navigation carries an exact, URL-encoded `cluster` query value. An explicitly requested configured direct cluster receives a fresh real connection probe in each routed session before detail reads; missing and externally managed identities remain selected without substitution or enabled administration. Opening the overview without an explicit identity keeps the existing untested default until the operator tests a connection. The `/workspaces/` prefix avoids the backend's existing `/eventbus-kafka/clusters` API route.

Destructive confirmations retain the cluster and topic identity captured when the action was opened. A later selection or route change cannot redirect a confirmed command. Inspectors show their frozen context. Configuration and broker reads are distinct: untested is not connection failure, an unavailable read is not a verified empty inventory, and a failed refresh retains matching earlier evidence with its diagnostic.

## Understand the evidence

`KafkaClusterSummary.HasTopicInventory` and `HasConsumerGroupInventory` record whether each inventory query succeeded. `KafkaPerformanceSnapshot.HasMetadataSnapshot` becomes true only after broker, topic, and consumer-group metadata reads all succeed. Counts from an incomplete capture must not be interpreted as verified zeros. Optional offset collection can still be incomplete after valid metadata, so retained counts, committed offsets, lag, and rates may remain null. The first capture establishes a baseline; rates need a later comparable offset sample. Retained-message estimates use the Kafka latest-minus-earliest offset range and do not count application acknowledgments.

`JmxEndpoint` is configuration evidence. No JMX collector currently exists in this console, and captures report `IncludesJmxMetrics = false` even when an endpoint is configured. Endpoint-derived capability declarations do not establish successful collection.

Each composing workbench creates its own `KafkaWorkbenchSession` through the scoped `KafkaWorkbenchSessionFactory`. Route wrappers key the composing owner by the exact cluster query so query transitions retire old operations and inspectors. The component owns the created session, provider cancellation, pending operations, dialog references, and render callbacks. Stop admission, cancel and drain actual work, close late dialogs, then retire the session. A retired route cannot stop a newer route's sampler. A capture that ignores cancellation is still drained; completion checks caller, session, and target cancellation before publishing evidence or returning success, including while linked-token callbacks are still draining. `EnablePerformanceAutoRefresh` defaults to true after the selected broker is verified; `PerformanceRefreshInterval` defaults to five seconds for each new routed session. Preferences changed in the performance workspace apply to that session.

## Upgrade persisted snapshots

Hosts using `UseEfCoreStore(...)` own their database migrations. Add the nonnullable `HasMetadataSnapshot` boolean column to `mo_eventbus_kafka_performance_snapshots` through the host's normal migration process. Initialize existing rows to false because their previous schema did not record collection validity; a fresh successful capture establishes verified evidence. The in-memory store and EF entity conversion preserve the same explicit validity flag. This schema change does not alter the EventBus transport or broker records.

Source owners are under `Monica.EventBus.Kafka/`: `Modules/ModuleEventBusKafkaUI.cs`, `UIEventBusKafka/Workspaces/KafkaWorkbench.razor`, `UIEventBusKafka/State/KafkaWorkbenchSession.cs`, `UIEventBusKafka/State/EventBusKafkaPageState.cs`, `UIEventBusKafka/State/KafkaPollingSession.cs`, `Services/KafkaPerformanceService.cs`, `Services/KafkaClusterService.cs`, and `Providers/EfCore/KafkaConsoleEntities.cs`. Non-rendering behavioral checks are under `tests/Test.Monica.EventBus.Kafka/`.
