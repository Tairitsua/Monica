# Health checks

Register probes inside the host's `builder.AddMonica(monica => { ... })` callback:

```csharp
builder.AddMonica(monica =>
{
    monica.AddHealthCheck(options =>
    {
        options.ReadinessPath = "/health";
        options.LivenessPath = "/alive";
    });
});
```

`AddHealthCheck()` registers the native ASP.NET Core health-check service and a sanitized `HealthCheckFacade`. On Web hosts it maps anonymous readiness (`/health`) and liveness (`/alive`) endpoints by default. Ready- and live-tagged checks are filtered separately; the built-in `monica.self` check carries both tags. That self check establishes process responsiveness; add application dependency checks with the matching tags when readiness must reflect a database, queue, or other prerequisite. Healthy and degraded responses are HTTP 200, unhealthy is HTTP 503, with a plain-text status body. Generic hosts retain the health service without endpoints. Paths must be distinct absolute application paths other than `/`, without query or fragment. Disable either endpoint via its `Enable*Endpoint` option if the host already owns that probe contract.

From application code, inject `HealthCheckFacade` and call `GetSnapshotAsync(HealthCheckScope.Readiness, cancellationToken)` to execute the readiness checks and receive a sanitized snapshot. `Liveness` selects live-tagged registrations; the default `All` includes every check. Check the returned `Result<HealthCheckSnapshot>` before reading its data, then inspect the snapshot's health status: successfully obtaining a snapshot does not mean every dependency is healthy. This query works without enabling HTTP endpoints.

`AddHealthCheckUI()` supplies a dashboard for the health snapshot and declares its health-check, localization, and shell dependencies. It does not change readiness or liveness semantics. Use `$monica-infra-ui` for shell hosting and access policy.

The `/health-checks` page evaluates readiness and liveness tags in the sanitized snapshot and shows each configured endpoint path. Its HTTP label projects the endpoint status from that snapshot. A check can contribute to both probes. Checks carrying neither tag appear in the auxiliary group. The probe summary includes shared and auxiliary tags as well as the two HTTP probes. The timing envelope compares nonhealthy registrations, or the slowest registrations in an all-healthy snapshot, against the wall duration of the whole concurrent run. Individual durations can overlap. Focus a lane and use Up, Down, Home or End to read exact durations. Enter opens the check’s captured evidence.

The registry combines name or description search, status, probe, exact tag and sort. Clearing its search field preserves the other filters. Use `?entry=<name>` to open a registration directly. Selecting a tag or probe in a sheet filters the registry. JSON copy includes the sanitized entry and its capture metadata. Redacted values remain redacted. Polling defaults to ten seconds and can be paused. The live status shows the cadence and seconds until the next automatic read. Manual refresh coalesces with an active read. The page checks current-circuit authorization before each facade query and retains its last successful snapshot if a refresh fails.

The details panel uses `MoSideSheet` from `$monica-infra-ui` (`references/visual-components.md`, "Modal evidence side sheets"): the component owns the native dialog portal, focus and dismissal, stationary title and Close action, and one scrolling body. The health session retains its selected entry, refresh and evidence lifetime.

For module graph and option diagnostics, add `AddModuleSystem()` and consume `ModuleDiagnosticsFacade`. For hosted-service state and heartbeat history, add `AddHostedService()` and use its registry or dashboard. These report different state from health probes. Read [host composition](../../monica-infra-hosting/references/host-composition.md) or [hosted services](../../monica-infra-hosting/references/hosted-services.md) for those contracts.

Check `Monica.HealthCheck/Modules/ModuleHealthCheck.cs` for current probe registration and endpoint behavior, and `Monica.HealthCheck/Facades/HealthCheckFacade.cs` for snapshot queries. The reference application at `examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api/Program.cs` composes health probes.
