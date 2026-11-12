---
name: monica-infra-configuration
description: Use when integrating Monica frozen Startup or Managed configuration, deployment inputs, file or EF Core stores, pure object validation, options diagnostics, reviewed mutations and rollback, or the configuration operator UI.
---

# Monica configuration

Choose Startup ownership for deployment inputs needed during host construction, and Managed ownership for store-backed operator changes. The application's frozen `AutonomousWork` policy controls automatic managed publication and initialization; Manual hosts can inspect values and perform guarded, reviewed edits without taking over deployment ownership. Read bootstrap guidance for composition, options guidance for validation and ownership, and runtime changes for reviewed writes and read-only Startup inspection.

| Task | Read |
| --- | --- |
| Read frozen Startup options before DI, select deployment sources, or compose a managed input plan and store | [Bootstrap and stores](references/bootstrap-and-stores.md) |
| Choose Startup or Managed ownership, declare pure object rules, validate an instance, or distinguish frozen/source/observed reports | [Options and validation](references/options-and-validation.md) |
| Inspect source precedence, preview complete mutation groups, change or roll back values, reload replicas, or use the operator UI | [Runtime changes](references/runtime-changes.md) |

For module implementation, use `$monica-framework`, then `$monica-development`; for building configuration UI components, use `$monica-ui-development`.

If a named skill is unavailable, use `$monica-guide` to resolve the verified Monica checkout (`source resolve --repository monica --json`) and read its canonical `skills/<name>/SKILL.md` and specified reference.
