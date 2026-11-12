# Lease refinement validation — 2026-10-28

Baseline: `b124f440f` on `codex/uat-native-architecture`. These results cover the subsequent scheduler API cleanup, Outbox delivery changes, common lease metrics and isolated Sentinel validation.

## Measurement conditions

- PostgreSQL 15 in a disposable loopback Docker container; actual Vastbase G100 V2.2 Build15 / openGauss 5.0 at the previously authorized UAT database endpoint, using fixture-owned temporary databases only. Application databases and deployment were unchanged. After the runs, a read-only check found zero `monica_jobs_test_*` databases; both disposable PostgreSQL/Redis containers and the Sentinel topology were removed.
- 1, 4 or 16 independent host containers in one .NET process (DI hosts, not Pods), sharing a database pool capped at three physical connections. This intentionally exercises contention; it is not a production cluster sizing result.
- Each measured case has one warmup and five samples of 128 messages, 512-byte payloads and a 32-attempt drain bound. The recording transport accepts immediately. Results exclude broker latency, consumer processing, CK parsing, startup and seeding.
- The baseline reproduces the previous serial candidate-scan / conditional-claim / acknowledgment protocol. The new implementation is measured at the unchanged default concurrency 1 and explicitly enabled concurrency 8. The latter includes parallel send scheduling, so it must not be described as a pure SQL optimization.
- Values below are medians across five samples. Drain P95 is a per-pass latency, not a per-message latency; passes can own different numbers of messages under contention. Raw per-host counts and samples are retained in the linked JSON.

## Delivery results

| Database | Hosts | Protocol | Concurrency | Messages/s | SQL commands / 128 messages | Elapsed ms | Drain P95 ms |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |
| postgres | 1 | Previous CAS | 1 | 403.76 | 261 | 317.02 | 86.18 |
| postgres | 1 | Native claim | 1 | 355.93 | 257 | 359.62 | 99.23 |
| postgres | 1 | Native claim | 8 | 1369.61 | 157 | 93.46 | 25.30 |
| postgres | 4 | Previous CAS | 1 | 659.47 | 659 | 194.10 | 50.75 |
| postgres | 4 | Native claim | 1 | 1180.12 | 262 | 108.46 | 106.33 |
| postgres | 4 | Native claim | 8 | 2059.08 | 160 | 62.16 | 61.37 |
| postgres | 16 | Previous CAS | 1 | 277.91 | 2237 | 460.58 | 120.11 |
| postgres | 16 | Native claim | 1 | 1199.42 | 288 | 106.72 | 103.18 |
| postgres | 16 | Native claim | 8 | 1927.87 | 176 | 66.39 | 62.55 |
| vastbase | 1 | Previous CAS | 1 | 8.23 | 261 | 15556.49 | 4621.69 |
| vastbase | 1 | Native claim | 1 | 6.32 | 257 | 20263.51 | 6074.03 |
| vastbase | 1 | Native claim | 8 | 27.06 | 158 | 4730.65 | 1400.64 |
| vastbase | 4 | Previous CAS | 1 | 13.01 | 647 | 9836.43 | 3284.26 |
| vastbase | 4 | Native claim | 1 | 14.10 | 262 | 9076.15 | 9026.19 |
| vastbase | 4 | Native claim | 8 | 31.24 | 162 | 4097.21 | 3990.60 |
| vastbase | 16 | Previous CAS | 1 | 7.05 | 2219 | 18155.25 | 4898.44 |
| vastbase | 16 | Native claim | 1 | 12.85 | 288 | 9962.66 | 9675.00 |
| vastbase | 16 | Native claim | 8 | 27.35 | 176 | 4679.53 | 3996.62 |

Raw results: [PostgreSQL](outbox-delivery-postgres.json), [Vastbase](outbox-delivery-vastbase.json).

Interpretation:

- postgres: at 16 hosts, default concurrency 1 delivers 4.32× baseline throughput with 87.1% fewer SQL commands. Explicit concurrency 8 delivers 6.94× baseline throughput. At one host and concurrency 1, throughput changes by -11.8%.
- vastbase: at 16 hosts, default concurrency 1 delivers 1.82× baseline throughput with 87.0% fewer SQL commands. Explicit concurrency 8 delivers 3.88× baseline throughput. At one host and concurrency 1, throughput changes by -23.2%.

The serial single-host case regresses in these measurements. The new path performs a native claim per free slot and isolates send scopes, whereas the previous path loads an entire candidate batch into one shared delivery scope; the benchmark does not separately attribute each source of overhead. Native-server load and network conditions also affect the observed latency. Reusing the claim context removes unnecessary construction without allowing a timed-out, cancellation-ignoring handler to share a context with a later send. Prefer concurrency 8 only when handler/transport overlap and completion reordering are acceptable; the default remains 1.

## Correctness and lifecycle evidence

- PostgreSQL and Vastbase: disjoint delivery across 16 hosts with pool size 3; a physically locked oldest row is skipped; ±7-day host-clock skew does not change native due filtering or retry scheduling; canceled/default-serial sends do not reserve later rows; timed-out sends keep their token until expiry; a hung slot does not stop other slots from being replenished; ambient transactions are rejected before claiming/sending.
- Native claims consume and dispose their result reader before any send starts. Claim expiry rounds upward to whole seconds to accommodate existing timestamp precision. A pass-local sequence cursor prevents immediate retry loops. Completion/retry still match row plus claim token; delivery remains at least once.
- Each concurrent or serial send owns a separate scope/context. A single claim context is reused only for sequential claim calls. No large backlog of unsent claims or claim-renewal supervisor is introduced.
- Lease metrics: acquire/renew/release outcomes and latency, one retirement observation per term, normal stop versus ownership loss, host factory isolation, simultaneous coordinator construction, cached-role reads, late replies, throwing listeners and release deadlines. Instrument publication is serialized by the winning per-factory lazy owner.
- The Vastbase fixture was corrected to use context-owned connections from its existing bounded data-source pool. GaussDB 10.0.0 includes an explicit data source connection string in EF provider-cache identity; distinct temporary databases previously accumulated providers and caused three fixture-initialization failures. The complete correctness rerun passed without suppressing warnings.

## Redis Sentinel fault injection

The opt-in scenario created one isolated Docker topology with three Redis processes and three Sentinels, killed the actual primary process, and observed a different promoted primary. The same Sentinel-managed client recovered a coordinator term and confirmed renewal after promotion in 8.08 seconds from failure injection. The initial term was retired: `true`.

A subsequent holder received a fresh term. Renew/release using prior term identities were rejected, retired cancellation tokens stayed canceled, no temporary leases remained and the owned container was removed. This is one recovery observation, not a latency SLA or a proof of global exclusivity under asynchronous Redis replication. No application/UAT Redis was faulted. [Raw evidence](sentinel.json); [reproduction guide](../../Test.Monica.Locker.Redis/SENTINEL.md).

## Code-size accounting

Compared with `b124f440f`, including comments and blank lines and counting the key-model rename:

| Production C# scope | Net lines |
| --- | ---: |
| Removed scheduler legacy grant API and adapter forwarding | -53 |
| Common coordinator metrics and consumer wiring | +128 |
| Outbox native claiming, bounded sends, registration and safety contracts | +150 |
| Total production C# | +225 |

Production C# has 435 added and 210 removed lines. Test C# adds a net 1,230 lines, including the opt-in comparison protocol and fault-injection harness. This change reduces duplicate role-lease APIs; it does not reduce total source size. Job execution/message claims remain with their business rows for atomic state transitions. They are not separate implementations of role election.

## Verification

- `dotnet build Monica.slnx -m -c Release`: passed with zero warnings and errors after final implementation/test changes.
- Standard sequential `scripts/run-tests.ps1 -NoBuild`: 41 non-UI projects passed, 3,651 tests passed and 32 explicit opt-in cases skipped. The subsequent claim-context reuse and metric-publication race fixes were rechecked through all Repository (144), JobScheduler (202 passed / 31 unconfigured opt-in skips), Locker (40), and ServiceDiscovery (31) tests.
- PostgreSQL delivery correctness + performance: 11 passed; the corresponding Vastbase cases were explicitly skipped in that process. Vastbase correctness: 10 passed, with only the unconfigured PostgreSQL counterparts skipped. Both native performance runs passed separately.
- Redis provider suite including the explicitly enabled Sentinel scenario: 8 passed. Later production changes affected claim-scope allocation and metric publication only; those were covered by the affected-suite recheck.
- `validate_agent_skills.py`, `sync_agent_skills.py --write`, `sync_agent_skills.py --check`, `test_agent_skills.py`, and `check_knowledge_impact.py --base b124f440f`: passed.
- Existing `Fips.Host` consumer Release build: passed, zero errors and 1,564 warnings from the unchanged FIPS source. No FIPS source/configuration or deployment changes were made.

## Reproduction

Use explicit test-server connections as described in [native database testing](../../README.md). The fixture creates and removes only its uniquely named temporary databases. Set `MONICA_OUTBOX_DELIVERY_PERF_OUTPUT` to an existing absolute directory and run:

```powershell
dotnet test tests/Test.Monica.JobScheduler/Test.Monica.JobScheduler.csproj -c Release --no-build --filter "FullyQualifiedName~ExternalOutboxLeaseTests.Delivery_"
```

The reusable tests do not read application configuration or retrieve credentials. Credential selection for this run was kept in a local, uncommitted runner and verified the existing authorized target before setting the test environment variable.
