# Project Application Specification

A repository-owned `<project>-application-spec` tells a developer who knows Monica what to do differently in this project. Use the [entrypoint template](../assets/project-application-spec/SKILL.md.template) in the repository's declared skill source and route to it from `AGENTS.md` or equivalent. The entrypoint loads `$monica-application`. A project following shared defaults needs only that entrypoint.

Include a rule only when the project has adopted it, the shared skills leave the choice open or require a scoped exception, and it changes how future code is written or reviewed. Typical entries select a product base type, restrict which layer may depend on it, or prescribe a local naming or placement convention. State when it applies, what to use and the boundary. Point to a representative implementation when it clarifies the choice. For an exception to a mandatory rule, keep its rationale and approved scope beside it. Follow [specification evolution](01-specification-evolution.md) for a proposed change.

Keep short rules in the entrypoint. Split a reference only when a developer can load it independently for a specific task. Read ordinary business behavior from its existing requirements and tests, topology from source and configuration, and operational procedures from their owner. Leave implementation details in code. Removing material from a skill does not require copying it into another document.

Update the spec when an adopted difference changes. Replace superseded rules in place and remove duplicates and obsolete entries. An ordinary feature, a new combination of Building Blocks or a migration task can ship without a spec edit.

For example, an application can use the shared Handler, DomainService and event contracts to implement a new business operation without adding a rule. If the project adopts a history-aware CRUD base for a defined set of adapters, record that choice once for future adapters. Changes to an individual adapter's filtering or business limits stay with that feature.
