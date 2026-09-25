---
name: monica-infra-configuration
description: Use when integrating Monica managed configuration, bootstrap input plans, file or EF Core stores, runtime reload and validation, or the configuration operator UI.
---

# Monica managed configuration

Choose bootstrap guidance when managed values determine host construction; choose runtime changes when inspecting or changing a running application's values.

| Task | Read |
| --- | --- |
| Select a managed store, compose an input plan, or read values before host construction | [Bootstrap and stores](references/bootstrap-and-stores.md) |
| Declare managed options, choose binding identity, or diagnose validation | [Options and validation](references/options-and-validation.md) |
| Inspect source precedence, change or roll back values, reload replicas, or use the operator UI | [Runtime changes](references/runtime-changes.md) |

For module implementation, use [development](../monica-development/SKILL.md); for building configuration UI components, use [UI development](../monica-ui-development/SKILL.md).
