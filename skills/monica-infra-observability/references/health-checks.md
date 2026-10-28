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

From application code, inject `HealthCheckFacade` and call `GetSnapshotAsync(HealthCheckScope.Readiness, cancellationToken)` to execute the readiness checks and receive a sanitized snapshot. `Liveness` selects live-tagged registrations; the default `All` includes every check. Check the returned `Res<HealthCheckSnapshot>` before reading its data, then inspect the snapshot's health status: successfully obtaining a snapshot does not mean every dependency is healthy. This query works without enabling HTTP endpoints.

`AddHealthCheckUI()` supplies a dashboard for the health snapshot and declares its health-check, localization, and shell dependencies. It does not change readiness or liveness semantics. Use `$monica-infra-ui` for shell hosting and access policy.

The `/health-checks` page separates readiness from the distribution of all registered checks. Readiness considers only `ready` tags: an unhealthy readiness check blocks traffic, while degraded readiness remains qualified ready. When no readiness checks are registered, the page explicitly reports missing readiness coverage rather than treating that absence as a confirmed dependency failure. The attention panel shows nonhealthy checks from the full snapshot, including checks outside readiness; opening one displays only the facade's bounded, sanitized entry evidence.

The registry combines name-or-description search, status tabs, and an exact registration-tag filter. Those filters change the visible entries, not readiness or the full-snapshot counts. The page refreshes every ten seconds and provides a coalesced manual refresh, with current-circuit authorization evaluated before each facade query. A failed refresh keeps the previous snapshot with a visible warning; revoked access hides operational content. Open details reconcile against the refreshed entry name and close when that registration disappears. Details contain keyboard focus; Escape, the close button, or the backdrop closes the panel and restores focus to its opener. This is point-in-time evidence, not stored health history or an availability percentage.

The details panel uses [MoSideSheet](../../monica-infra-ui/references/visual-components.md#modal-evidence-side-sheets): the component owns the native dialog portal, focus and dismissal, stationary title and Close action, and one scrolling body. The health session retains its selected entry, refresh and evidence lifetime.

For module graph and option diagnostics, add `AddModuleSystem()` and consume `ModuleDiagnosticsFacade`. For hosted-service state and heartbeat history, add `AddHostedService()` and use its registry or dashboard. These report different state from health probes. Read [host composition](../../monica-infra-hosting/references/host-composition.md) or [hosted services](../../monica-infra-hosting/references/hosted-services.md) for those contracts.

Check `Monica.HealthCheck/Modules/ModuleHealthCheck.cs` for current probe registration and endpoint behavior, and `Monica.HealthCheck/Facades/HealthCheckFacade.cs` for snapshot queries. The reference application at `examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api/Program.cs` composes health probes.
