# Isolated Sentinel failover check

The opt-in `ExternalRedisSentinel` scenario creates one uniquely named and labeled Docker container with three Redis and three Sentinel processes. It publishes six dynamically chosen loopback ports and uses the same port numbers inside the container, so Sentinel's loopback announcements are reachable from a Windows or Linux test process. No existing Redis endpoint or application configuration is accepted.

Use a local Docker engine and cache the desired Redis image before running. The default is `redis:7-alpine`; `MONICA_LEASE_TEST_REDIS_IMAGE` may select another already cached image. The fixture starts the inspected image ID with `--pull never`, verifies its ownership label before fault injection/deletion, kills only the discovered primary process, and removes its container in `finally`. It creates no volumes or custom networks.

After the normal sequential Release build:

```powershell
$env:MONICA_LEASE_TEST_SENTINEL = '1'
$env:MONICA_REDIS_SENTINEL_OUTPUT = 'C:/test-results/redis-sentinel.json'
dotnet test tests/Test.Monica.Locker.Redis/Test.Monica.Locker.Redis.csproj -c Release --no-build --filter 'Category=ExternalRedisSentinel'
Remove-Item Env:MONICA_LEASE_TEST_SENTINEL, Env:MONICA_REDIS_SENTINEL_OUTPUT
```

The optional JSON output path must be absolute and its parent directory must already exist; this is checked before starting Docker. The scenario has a two-minute deadline, bounded Docker/client calls, and a separate twenty-second cleanup deadline that remains active after test cancellation. Cleanup verifies that the container is absent. Docker control failures are reported, including the owned container name if startup and cleanup both fail. Port allocation is isolated but not atomically reserved through Docker startup; a concurrent bind fails startup and triggers owned-container cleanup.

The scenario verifies that Sentinel promotes a different primary, the existing Sentinel-managed connection recovers, the actual `LeaseCoordinator` resumes confirmed renewal, and a successor receives a fresh term. Old renew/release attempts are rejected and retired cancellation tokens stay cancelled. A fast failover may preserve the current lease; a slow or ambiguous renewal may retire it. Both outcomes are accepted and reported. A readiness-only `WAIT` confirms initial replication; it does not make Redis strongly consistent.

This test proves bounded recovery in the owned topology. It does not establish global mutual exclusion during asynchronous replication, partitions, or acknowledged-write loss. It intentionally retains the SQL transaction boundary for business writes and does not run destructive failover against UAT or DEV.
