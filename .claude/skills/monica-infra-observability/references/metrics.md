# OpenTelemetry metrics

Inside the host's `builder.AddMonica(monica => { ... })` callback, select the export or inspection mode explicitly:

```csharp
builder.AddMonica(monica =>
{
    monica.AddOpenTelemetry()
        .UseOtlpExporter();
});
```

`AddOpenTelemetry()` wires metrics, the `Monica.*` meter pattern by default, and ASP.NET Core, HttpClient, and runtime instrumentation. It does not enable an exporter by default. Choose `UseOtlpExporter(endpoint?)` for an external collector; without an explicit endpoint the OpenTelemetry SDK uses its environment configuration. `UsePrometheusEndpoint(path: "/metrics")` adds a Web scraping endpoint. `UseConsoleExporter()` is useful locally. `UseInProcessCollector()` enables a bounded per-instance snapshot and, on Web hosts with the minimal-API switch, `/opentelemetry/snapshot`; this is not durable or cross-instance storage. `AddMeter(pattern)` subscribes additional names or wildcard patterns. The exporter and in-process collector serve different retention needs and can coexist.

A generic host keeps metrics collection and configured non-Web exporters without mapped scrape or snapshot routes. Optional Web snapshot routes follow the host's `EnableMinimalApiByDefault` or the module override, and that host default is `false`; select an external exporter when data must survive restarts or combine instances.

Set `ResourceServiceName`, `ResourceServiceVersion`, or `DeploymentEnvironment` when the collector needs stable service identity; unset name and version use the host's application defaults. The built-in instrumentation flags default to enabled. `UseInstrumentationMetersInProcessCollector(false)` narrows the local dashboard to explicitly selected `MeterPatterns` while leaving SDK instrumentation/export unchanged. `AddOpenTelemetryUI()` adds a shell dashboard and enables the in-process collector through its module dependency; the UI is a local snapshot view, not a metrics database. If an exporter receives no measurements, check that a meter pattern matches the instruments and that the exporter was selected. If only `/opentelemetry/snapshot` is absent, check the in-process collector and Minimal API switch separately.

The following combined composition is valid for a Web application:

```csharp
monica.AddHealthCheck();
monica.AddLogging(options => options.EnableFileSink = false);
monica.AddExecutionTiming().UseBackgroundBatchAggregation();
monica.AddOpenTelemetry().UsePrometheusEndpoint().UseInProcessCollector();
```

For the semantics of the other registrations in this example, read [health-checks.md](health-checks.md), [logging.md](logging.md), or [execution-timing.md](execution-timing.md) as needed. For module metrics instrumentation rather than host configuration, use `$monica-opentelemetry`.

Check `Monica.OpenTelemetry/Modules/ModuleOpenTelemetry.cs` for current instrumentation and exporter registration and `Monica.OpenTelemetry.UI/Modules/ModuleOpenTelemetryUI.cs` for dashboard composition. The reference application at `examples/Monica.ReferenceApplication/src/AppHost/Monica.Reference.Api/Program.cs` composes Prometheus metrics.
