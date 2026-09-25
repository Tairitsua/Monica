---
name: monica-infra-observability
description: Configure and consume Monica health probes, logging, execution timing, metrics exporters, and runtime diagnostics in an application. Use monica-opentelemetry for authoring new module instruments.
---

# Monica observability

Health, logging, execution timing, tracing, and metrics have independent registration and lifecycles; choose the signal that answers the operational question.

| Task | Read |
| --- | --- |
| Decide whether the host and its dependencies are ready or live | [Health checks](references/health-checks.md) |
| Record events and failures or inspect local log files | [Logging](references/logging.md) |
| Measure slow or running business operations | [Execution timing](references/execution-timing.md) |
| Follow a controller, execution, database, or remote call chain | [Tracing](references/tracing.md) |
| Inspect workload trends or export measurements across instances | [Metrics](references/metrics.md) |

For module-graph and worker-state diagnostics, use [hosting](../monica-infra-hosting/SKILL.md); for authoring instruments, use [OpenTelemetry development](../monica-opentelemetry/SKILL.md).
