---
name: monica-framework
description: Router skill for Monica framework development. Use when working on Monica infrastructure modules, module architecture, Blazor UI modules, documentation, requirement design, UI audits, or Monica test conventions and choose the correct Monica framework companion skills.
---

# Monica Framework

Use this skill as the entry point for work on Monica itself: infrastructure modules, shared UI, framework documentation, requirements, and test conventions.

## Routing

- Exact Monica source or an unavailable named skill/reference: use `$monica-guide source resolve --repository monica --json` as a read-only locator, then read canonical `skills/<name>/SKILL.md` and the specified reference.
- Module architecture, folder boundaries, Facades, Providers, public/internal placement, page decomposition: use `$monica-architecture`.
- Module registration, `Res` and `Res<T>`, services, registration extensions, hosted services, runtime module behavior: use `$monica-development`.
- Independent NuGet packages, publisher-first IDs, third-party module keys, compatibility branding, licensing, scaffolding, and publishing: use `$monica-third-party-module-development`.
- Blazor or MudBlazor implementation, CSS isolation, themes, browser storage: use `$monica-ui-development`.
- Monica UI localization/i18n resources, hardcoded user-facing text, UI registry keys, and localization validation: use `$monica-ui-localization`.
- UI concept design or interactive HTML prototypes before implementation: use `$monica-ui-design`.
- Theme-first component compliance and hardcoded-style audits: use `$monica-ui-audit`.
- Monica user documentation, module docs, guides, and zh-CN documentation: use `$monica-docs-authoring`.
- Startup, routing, UI, persistence, or delivery failures: use `$monica-troubleshooting` to select the owning diagnostic reference before changing implementation.
- Requirement gathering and architecture planning for Monica modules: use `$monica-requirement-design`.
- OpenTelemetry-compatible meters, counters, histograms, gauges, diagnostic snapshots, or telemetry ownership: use `$monica-opentelemetry`.
- Monica unit-test layout, shared test infrastructure, xUnit, or bUnit conventions: use `$monica-unit-testing`.
- Bridge-based UI inspection through a runnable host: use `$monica-ui-bridge-debug`.
- Read the affected usage contract before changing implementation: `$monica-infra-hosting`, `$monica-infra-persistence`, `$monica-infra-messaging`, `$monica-infra-jobs`, `$monica-infra-configuration`, `$monica-infra-observability`, `$monica-infra-ai`, `$monica-infra-web`, or `$monica-infra-ui`. Update that contract when behavior changes.

## Working Rule

Load only the granular skill that matches the active task. Combine skills when the task crosses boundaries, such as `$monica-architecture` plus `$monica-ui-development` for a new UI module.
