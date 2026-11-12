# Redis lease authority and advisory Outbox maintenance

Redis reduced coordination latency and SQL maintenance overhead in this local native-provider comparison. With 16 replicas and a PostgreSQL pool of 16, renewal P95 was **8.717 ms for PostgreSQL and 0.983 ms for Redis**. Cached-holder Outbox cleanup of 4,000 expired messages took a median **17.950 ms with SQL transaction guards and 13.426 ms with advisory Redis coordination**. These are different safety contracts: Redis is appropriate for repeat-safe maintenance, while scheduling, execution recovery, and delivery claims keep their database guards.

## Environment and measurement

- Captured on October 28, 2026 (UTC+08; raw files preserve UTC capture times).
- Release .NET 10 client on Windows; isolated local Docker Linux containers, PostgreSQL **15.19** and Redis **7.4.12**, reached through published loopback ports.
- Production `RelationalLeaseStore` and `RedisLeaseStore` implementations; no mocked network or persistence. StackExchange.Redis **2.10.1**.
- 1, 4, and 16 logical replicas. Each Redis replica owns a separate multiplexer. PostgreSQL replicas share the fixture's connection pool, tested with limits **3** and **16**. This makes SQL connection contention visible; it is not a claim that a deployed cluster shares one client pool.
- Coordinator scenarios warm each replica, then run 100 sequential operations per replica concurrently. Standby repeatedly contends for an occupied resource; renewal uses one held resource per replica, avoiding intentional same-row contention. Timings include client scheduling and connection waits. SQL interceptors count commands/explicit transactions; Redis profiling counts actual `EVAL`/`EVALSHA` commands.
- Outbox uses the real dispatcher's cleanup/status loops with a shared SQL pool of 3. Each pass starts with 12,000 retained and 4,000 expired acknowledged messages, 512-byte bodies, batch size 1,000, the production 2-second cleanup budget and 5-second operation deadline. Test-owned data is truncated, reseeded, and analyzed outside each measurement.
- The Outbox harness advances only work cadence; server time, monotonic lease validity, renewal, and operation deadlines remain real. Warm passes wake the cached holder; concurrent standby traffic is measured separately above. One cold pass and five cached-holder passes are recorded per authority/replica count. Statistics query counts are measured separately, once per replica group.

## Coordinator latency

P95 milliseconds (100 operations per replica per scenario):

| SQL pool | Replicas | Standby SQL | Standby Redis | Renewal SQL | Renewal Redis |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 3 | 1 | 0.892 | 0.269 | 3.697 | 0.259 |
| 3 | 4 | 1.572 | 0.436 | 5.671 | 0.441 |
| 3 | 16 | 6.798 | 0.868 | 22.996 | 0.860 |
| 16 | 1 | 0.966 | 0.492 | 3.424 | 0.321 |
| 16 | 4 | 1.166 | 0.399 | 4.181 | 0.400 |
| 16 | 16 | 4.505 | 1.188 | 8.717 | 0.983 |

Each SQL standby probe issues one command. Each SQL renewal issues three commands and one explicit transaction, preserving the relational protocol. Each Redis operation uses one Lua command; it does not access SQL. Increasing the SQL pool reduces queuing, but Redis still has lower latency in this workload. Redis results vary between the two independent runs; only SQL pool configuration changes between them.

## Outbox cleanup and statistics

All cases deleted exactly 4,000 expired messages, retained all 12,000 recent messages, and executed five bounded deletes (four full batches plus an empty probe).

| Replicas | Cold SQL (ms) | Cold Redis (ms) | Warm SQL median (ms) | Warm Redis median (ms) |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 124.172 | 33.191 | 19.610 | 13.014 |
| 4 | 59.692 | 19.163 | 20.107 | 13.539 |
| 16 | 65.730 | 16.981 | 17.950 | 13.426 |

Cold measurements include first election and loser contention, so their single samples are descriptive rather than stable estimates. Warm medians use five passes. At 16 replicas, warm cleanup elapsed time decreased by approximately **25.2%**. The database deletes remain the main end-to-end work; the approximately 8.9-fold renewal latency difference does not imply an equivalent cleanup throughput increase.

| Cost per cached-holder cleanup pass | SQL authority | Redis authority |
| --- | ---: | ---: |
| Bounded SQL deletes | 5 | 5 |
| SQL lease validation reads | 5 | 0 |
| Explicit SQL transactions | 5 | 0 |

Zero explicit transactions means each Redis-coordinated delete uses PostgreSQL's normal statement transaction. It does not mean the database executes without transaction semantics. Lease acquisition/renewal happens independently; Redis does not perform a fake Redis-check/SQL-write fence before each delete.

For both authorities, each statistics pass executed **one aggregate query** at 1, 4, and 16 replicas. Low-frequency statistics therefore remain single-holder work under healthy coordination.

## Correctness coverage and limits

Native tests separately cover concurrent acquisition, same-holder ABA protection, real TTL expiry, renewal rejection after expiry, stale release rejection, literal scope isolation, nonzero Redis database observation, unavailable connections, keyed registration, two-host ServiceDiscovery leadership, and an old/new Outbox cleanup overlap with a pending message still deliverable. Controlled-clock ServiceDiscovery tests cover independent renewal, paused-host expiry, ambiguous renewal failure, initialization cancellation, and draining old work before the next term.

The standard Release build completed with zero warnings. The standard non-UI gate passed all 41 projects with native PostgreSQL and Redis enabled; optional Vastbase/performance cases were skipped there. The coordinator comparison (two pool configurations) and this Outbox comparison passed in separate opt-in runs. No timing threshold is enforced as a correctness assertion.

This is a local latency/command-cost sample, not a production capacity claim or a Redis failover proof. It does not measure cross-region latency, Cluster resharding, Sentinel failover, thousands of lease resources, or a competing application workload. SQL and Redis authorities intentionally provide different failure guarantees. Redis replication is asynchronous; failover can lose an acknowledged grant and allow overlapping holders. `WAIT` does not make Redis a strongly consistent CP store. See the official [distributed-lock guidance](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/) and [replication documentation](https://redis.io/docs/latest/operate/oss_and_stack/management/replication/).

## Reproduction and raw evidence

Run from the repository root with disposable native services. The PostgreSQL account must be allowed to create/drop the fixture's isolated databases. Use a dedicated Redis test instance.

```powershell
dotnet build Monica.slnx -m -c Release
$env:MONICA_JOBSCHEDULER_TEST_POSTGRES = '<PostgreSQL admin connection string>'
$env:MONICA_LEASE_TEST_REDIS = '<Redis endpoint>'
$env:MONICA_REDIS_LEASE_PERF_OUTPUT = '<absolute-existing-directory>/redis-coordinator.json'
$env:MONICA_ADVISORY_OUTBOX_PERF_OUTPUT = '<absolute-existing-directory>/redis-outbox.json'
dotnet test tests/Test.Monica.JobScheduler/Test.Monica.JobScheduler.csproj -c Release --no-build --filter 'FullyQualifiedName~ExternalLeaseProviderPerformanceTests|FullyQualifiedName~MaintenanceAuthorities_ShouldCompareColdElectionAndCachedCleanupWithRealServerClocks'
```

The pool-16 case writes `redis-coordinator.pool16.json` alongside the requested coordinator output. Implementation: [coordinator workload](../../Test.Monica.JobScheduler/Stores/External/ExternalLeaseProviderPerformanceTests.cs), [Outbox workloads](../../Test.Monica.JobScheduler/Stores/External/ExternalOutboxLeaseTests.cs).

Raw files are copied byte-for-byte from successful test outputs, preserving all timestamps, samples, and command counts:

- [Coordinator, SQL pool 3](redis-coordinator.json)
- [Coordinator, SQL pool 16](redis-coordinator.pool16.json)
- [Outbox cleanup and statistics](redis-outbox.json)
