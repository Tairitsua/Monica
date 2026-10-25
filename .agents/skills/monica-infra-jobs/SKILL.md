---
name: monica-infra-jobs
description: Use when adding Monica JobScheduler recurring or triggered jobs, choosing its store and scope, or configuring startup seeders and their readiness behavior.
---

# Monica jobs and seeders

Choose JobScheduler for recurring or on-demand work with managed execution history; choose Seeder for finite startup work with dependencies and readiness requirements.

| Task | Read |
| --- | --- |
| Register recurring or typed triggered jobs, select storage and scope, or enqueue application work | [Scheduler](references/scheduler.md) |
| Inspect execution history, trigger or cancel work, change policy, or expose the operator UI | [Scheduler operations](references/scheduler-operations.md) |
| Declare startup seeders, order dependencies, or diagnose readiness | [Startup seeding](references/startup-seeding.md) |

For application job placement, use ProjectUnit development in `$monica-application-project-unit-development`. For framework module or worker implementation, use `$monica-framework`, then `$monica-development`.

If a named skill is unavailable, use `$monica-guide` to resolve the verified Monica checkout (`source resolve --repository monica --json`) and read its canonical `skills/<name>/SKILL.md` and specified reference.
