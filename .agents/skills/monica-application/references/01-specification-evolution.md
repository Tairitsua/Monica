# Evolve an Application Specification

Use this reference when source facts drift or a requirement cannot be met within the existing architectural contracts. Approval of a feature authorizes implementation through compliant ProjectUnit composition. It authorizes an architectural deviation only when the human's decision identifies that deviation and its scope.

## Try Existing Composition First

Map the required state, rules, entry points, persistence, delivery, and lifetime to existing ProjectUnits and documented infrastructure capabilities. Combine them within their adopted contracts before proposing a new abstraction or specification rule. This is normal design work, not an extra approval document or mandatory checklist to show for every task.

A new business workflow, concrete handler, event, job, state machine, or combination of those units is not a new architectural role. Selecting documented transaction modes, independent operation scopes, Outbox/Inbox delivery, or job execution is not a new execution model. Here **execution model** means the framework-level contract for dispatch, scope ownership, transaction participation, commit/rollback, or recovery; changing the business sequence while preserving those contracts stays within the existing model. Each capability's prerequisites still apply when composed.

For example, replacing a transaction that spans slow remote I/O with a documented nontransactional orchestrator and independently committed local operations is ordinary composition when the requested business consistency can be preserved. Adding durable delivery through existing event contracts is also ordinary composition. The absence of that exact end-to-end example is not a reason to create a specification or request architectural approval.

Business changes such as returning an acceptance acknowledgement before processing finishes may need clarification or updates to existing requirements and acceptance criteria. Resolve that product-contract question within the user's task and existing authorization; do not label it an application-architecture exception solely because the observable behavior changes.

## Classify the Change

| Change | Action |
| --- | --- |
| Verified factual correction, such as a moved file, an existing dependency, or a renamed domain within approved patterns | Correct the project specification autonomously with source/test evidence. Preserve the distinction between current facts and approved intent. |
| New concrete units or a new combination of approved roles and capabilities, including documented execution and transaction choices | Implement and validate it within the authorized behavior. No new specification, architectural approval, or Workflow Change is required merely for the combination. Update existing project facts or behavioral requirements only where they become inaccurate. |
| A new architectural responsibility, layer, dependency direction, reusable base family, framework execution contract, or local deviation from a mandatory shared/project rule | Establish why existing composition cannot meet the requirement, then prepare the smallest rule change and obtain a human decision before adopting it. |
| Existing implementation outside the approved specification | Describe the known migration gap and its evidence. Do not silently rewrite a rule to bless the drift or spread it to new code. |

A new concrete subclass in an already adopted base family is routine implementation. Adding a reusable product base family is an architecture change even if its code is short. A fact correction must not erase the rule the source currently violates.

## Make the Decision Concrete

Before requesting architectural approval, identify the unmet use case, link the exact rule that would change or the architectural contract that is missing, and explain the concrete limitation of the available configuration, extension points, inheritance, or composition. A changed business sequence, a new type name, or a missing example is insufficient evidence. If no contract change is needed, continue ordinary implementation instead of inventing a specification gap.

For an actual gap, show the smallest proposed contract change and the affected consumers and files. Include lifetime/ownership and validation where they matter. Keep this proportional: a local placement exception needs its precise boundary; a new execution model needs its execution and failure semantics.

The human chooses adoption, revision, or rejection. Record the approved scope, rationale, and any conditions beside the owning rule, with a link or reference to existing evidence. Reuse Git history, a PR/discussion, or the project's existing decision record. When a relevant governed Workflow Change or decision flow already applies, record the confirmed decision through that flow's established semantic operations. Otherwise retain the human authorization, rule rationale, and existing history without creating a Change solely for specification maintenance. An existing explicit approval remains valid within its agreed scope; do not request it again for the same pattern. Do not create a new governance engine, approval schema, or ledger.

## Update the Correct Owner

- Generalizable Monica application patterns belong in the relevant shared skill and its examples/tests.
- Application topology, conventions, specializations, and local deviations belong in the project specification skill.
- A project-only exception does not change the shared default. State exactly which rule it deviates from, which projects/types may use it, and what remains required in that scope.

Once approved, update the owning specification and affected implementation, routes, examples, and verification together. Keep the rationale next to the rule. Summarize only unresolved migration gaps as current gaps; durable change history belongs in existing history rather than accumulating session narratives in the skill.
