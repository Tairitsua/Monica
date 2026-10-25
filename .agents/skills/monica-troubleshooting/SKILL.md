---
name: monica-troubleshooting
description: Diagnose Monica startup failures, missing routes, Blazor 404s or inactive pages, failed saves, delivery faults, configuration errors, and missing runtime signals. Use when a failure needs symptom-based routing to the owning infrastructure guidance.
---

# Monica troubleshooting

Identify the running Monica and .NET versions, actual host/environment, failed URL or operation, and first relevant error. Select the failed boundary below; read only the matching reference before changing configuration.

| Symptom | Read in the named skill |
| --- | --- |
| Guide installation, skill drift, or source binding fails | `$monica-guide` status/doctor guidance |
| Blazor script 404, empty CSS, or visible but inactive controls | `$monica-infra-ui` → `references/troubleshooting/static-assets.md` |
| UI navigation works but direct access or refresh returns 404 | `$monica-infra-ui` → `references/troubleshooting/routing-and-refresh.md` |
| Host composition, missing DI service, or role activation fails | `$monica-infra-hosting` composition, dependency-injection, or application-model reference |
| Save, transaction, outbox, or migration behaves unexpectedly | `$monica-infra-persistence` → `references/troubleshooting.md` |
| API call, event delivery, DataChannel, or SignalR fails | `$monica-infra-messaging` reference for that transport |
| Managed values are missing, rejected, or stale | `$monica-infra-configuration` bootstrap, validation, or runtime-change reference |
| Job does not run or startup readiness never completes | `$monica-infra-jobs` scheduler operations or startup-seeding reference |
| HTTP endpoint is absent or access is denied | `$monica-infra-web` endpoints, visibility, or access-control reference |
| Logs, traces, metrics, or health evidence are missing | `$monica-infra-observability` reference for that signal |
| AI provider, retrieval, runtime skill, or MCP fails | `$monica-infra-ai` reference for that capability |

If a selected skill is absent, use `$monica-guide` (`source resolve --repository monica --json`) to read its canonical `skills/<name>/SKILL.md` and selected reference from the verified checkout.

Confirm the cause through the selected checks and verify the original operation after fixing it. Inspect exact source for undocumented cases. When canonical edits are in scope, use `$monica-docs-authoring` to update the owning case and symptom route; otherwise report the reusable finding.
