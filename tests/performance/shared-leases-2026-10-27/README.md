# Shared lease measurements — 2026-10-27

This report records the WorkTree1 refactor on `codex/uat-native-architecture`, based on `0001f4149a25bbf1c1dd6c15b476a36f4487780b`. It measures the common relational role table and actual Outbox maintenance hosts. It does not measure production throughput, network partitions, long-running database bloat, or the ServiceDiscovery state-store election. UI work is outside this change.

## Environment and method

- Local Docker PostgreSQL 15.19 (`postgres:15-alpine`), Npgsql, EF Core 10.0.12, .NET 10 Release builds on Windows. One dedicated test server; each scenario creates and drops its own database.
- The application connection pool is deliberately capped at **3** shared connections. Thus 16 replicas include pool contention; they do not have 16 independent unlimited pools.
- Coordinator workload: 1, 4, and 16 independent production store instances, warmup followed by 100 operations per instance. The same harness was run before production changes and after the refactor. One additional after-run populated 10,000 unrelated role rows before repeating the workload.
- Outbox workload: 1, 4, and 16 independently composed hosts; 4,000 expired and 12,000 retained acknowledged rows, 512-byte message bodies, 1,000-row batches, two-second pass budget. The test-only original algorithm and the new worker run against reseeded data. The new pass includes **cold election and transaction guards**. Fake time triggers real worker loops; SQL, transactions, and measured elapsed time remain real. Status is sampled once after cleanup, and delivery remains active on every host.
- The tables retain one local baseline run and the final verification runs after simplification. Earlier development runs showed the same command counts and a similar latency direction. These are observations, not statistically established capacity guarantees; command counts and safety assertions are stronger regression signals.

## Coordinator results

| Scenario | Replicas | Before P95 (ms) | Shared table P95 (ms) | Shared table + 10,000 unrelated roles P95 (ms) |
| --- | ---: | ---: | ---: | ---: |
| Active-holder standby | 1 | 1.0883 | 0.9277 | 1.0971 |
| Active-holder standby | 4 | 1.6377 | 1.2605 | 1.3552 |
| Active-holder standby | 16 | 5.5502 | 5.0061 | 5.0667 |
| Independent role renewal | 1 | 5.8494 | 3.8038 | 3.9963 |
| Independent role renewal | 4 | 6.2848 | 5.9301 | 5.3943 |
| Independent role renewal | 16 | 31.5241 | 18.4483 | 19.0524 |

Standbys remain **one indexed SELECT and zero transactions per attempt**. Renewal changes from **four SQL commands to three**, with one short transaction in both designs. At 16 instances, 1,600 renewals used 6,400 commands before and 4,800 after; elapsed time changed from 2,329.0 ms to 1,610.3 ms. The larger shared table used the same command/transaction counts. The result supports retaining per-resource primary-key access; it does not establish that arbitrary table growth or write contention is free.

## Outbox results and cost

| Replicas | Original DELETE statements | Coordinated DELETE statements | Original elapsed (ms) | First coordinated pass (ms) | Lease-related reads during the coordinated pass |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 5 | 5 | 52.9035 | 62.6422 | 8 |
| 4 | 8 | 5 | 19.3478 | 35.3124 | 14 |
| 16 | 20 | 5 | 24.0488 | 109.1453 | 42 |

All cases removed exactly 4,000 expired rows and preserved 12,000 retained rows. The coordinated worker used four full batches and one empty probe, regardless of replica count. All hosts together executed one scheduled status query. Lease-related reads count statements mentioning `MonicaLeases`, including election and transaction guards; they exclude separate database-clock queries, so this column is **not total SQL cost**.

The first coordinated pass is slower in this small local workload. Election, conflict resolution, and ownership validation cost more than deleting a few cached rows. The benefit is suppression of redundant maintenance work and bounded ownership at higher replica counts, not a promise of faster individual cleanup passes. Retained-table scans and expensive backlog statistics are the target; evaluate realistic retention/backlog sizes and remote database latency before production sizing. Standbys still perform cheap role probes every configured retry interval, and holders renew independently. No lifetime connection or global table lock is introduced.

## Correctness evidence

The native suite checks one winner under 16 concurrent acquisitions, fresh terms for a returning holder, rejection of stale renew/release, and independent resources while another lease row is locked. A renewal blocked on a row lock observes database time after the wait and cannot revive an expired term. An actual Outbox cleanup transaction held at the DELETE boundary allows a different native connection to deliver a pending message. Mapping tests create Outbox tables while excluding the scheduler-owned common table from the second context's schema creation.

Deterministic runtime tests cover independent renewal, process pause, forward/backward wall time, late provider results, stalled I/O without accumulating requests, and disposal during acquisition. Cleanup tests cover retention boundaries, bounded batches, rollback before a batch commit, and preservation of earlier committed batches. These scenarios exercise correctness separately from the measured loops.

PostgreSQL native scenarios were run. No Vastbase test endpoint was configured; those scenarios were skipped. SQLite unit coverage is not evidence for Vastbase/native distributed locking.

Final repository verification: `dotnet build Monica.slnx -m -c Release` completed with **zero warnings and errors**. `pwsh -File scripts/run-tests.ps1 -NoBuild` passed **3,613 tests across 40 non-UI projects**, with 14 unavailable Vastbase cases and two opt-in measurements skipped in that gate. Both opt-in measurement tests then passed separately, including the additional 10,000-role run. Skill validation, projection synchronization, all four skill-owned test suites, and the knowledge-impact check against the base revision also passed. No UI rendering tests were run.

## Reproduction and raw data

Use the [native test instructions](../../README.md#opt-in-native-lease-tests-and-measurements). Preserve database version, pool limit, build mode, workload size, and logging configuration when comparing another run. Run the native suite and performance workloads sequentially so they do not contend with a separate build/test process.

- [Coordinator baseline](coordinator-before.json)
- [Coordinator shared table](coordinator-after.json)
- [Coordinator with 10,000 unrelated roles](coordinator-after-10000-roles.json)
- [Outbox original versus cold coordinated pass](outbox.json)

The owning migration and coordinated deployment cutover are described in the [lease usage contract](../../../skills/monica-infra-hosting/references/leases.md). This repository change does not apply migrations to a consumer or production database.
