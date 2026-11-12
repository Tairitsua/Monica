# Organize knowledge by when it is needed

Use this guide when maintaining canonical skill entrypoints and references. A skill owns a capability; its files divide the information needed to select and perform work.

## Four responsibilities

| Layer | Responsibility |
| --- | --- |
| Frontmatter description | Activation: when this skill applies and which adjacent tasks belong elsewhere. |
| `SKILL.md` | Selection: which reference or capability fits the current task. |
| Task references | Execution: public operations, prerequisites, contracts, examples, and diagnosis needed for the selected task. |
| Source and behavioral tests | Evidence: establish current behavior and resolve undocumented or contradictory cases. |

An entrypoint is loaded when the skill is activated. Put selection knowledge there so the agent can choose a reference without first opening another introduction. Ask: **would removing this sentence make the agent choose the wrong guidance?** If the sentence instead explains how to perform the operation, it belongs in the owning reference.

For example, the distinction between local database work and delivery after commit helps select persistence guidance. Validation defaults, provider configuration, and transaction mechanics belong in their execution references.

## Infrastructure entrypoints

Use a concise `Task | Read` table in `monica-infra-*` entrypoints. Express the decision in the task column and link directly to its owner. Distill capability selection into this table and, when needed, a short preceding explanation; do not move an entire overview into the entrypoint. Route neighboring work only where confusion is likely.

Keep the body within **26 physical lines and 300 whitespace-separated words**, excluding frontmatter and outer blank lines. The same budget applies to the cross-capability `monica-troubleshooting` router. These are context-budget ceilings, not targets or proof of quality. Keep readable paragraphs and table rows; move execution detail to its owner instead of packing it into long lines. The validator checks both limits so joining paragraphs cannot hide growth. References have no corresponding quota.

A reminder is an exception for a consequential distinction: at most one short sentence with a link to its owner, such as "A flush is not a commit; see [transactions](../../monica-infra-persistence/references/transactions.md)." Do not append its mechanism, default values, sequence, or exceptions. Each contract has one authoritative explanation; the reminder must not become another place to maintain that explanation.

## References follow their purpose

`overview.md` has no required or privileged role. Merge pure introduction and navigation into entrypoint selection. Give a substantive setup procedure a task-specific name. Keep a separate shared-concept reference only when its explanation is substantial and several tasks benefit from loading it conditionally. Never require an introductory hop for every task.

Split or merge references by independently selectable work, prerequisites, and reasons for change. A setup guide, transaction contract, and diagnostic guide need different structures. Do not impose a section template, file count, or repeated host setup on every topic. Keep necessary prerequisites explicit, using direct links where shared setup already has an owner.

Preserve the useful public call sequence and observable outcome for operational guidance. Explain application-owned types and placeholders. Keep defaults, ownership, failure semantics, and source/test evidence with the contract they qualify. Diagnostic guidance maps symptoms to checks and the owning explanation. A reference should let an ordinary consumer perform its task without repeatedly rediscovering documented behavior in implementation files.

## Troubleshooting ownership

`monica-troubleshooting` selects the failed boundary across capabilities. Concrete diagnostic knowledge stays in the owning `monica-infra-*` reference, where implementation changes already trigger the knowledge-impact check. Add symptom routes to that infra entrypoint so a known UI, persistence, or messaging fault can reach its case directly. Use named skill routes with exact reference paths for targets absent from a supported profile; the application and framework routers explain source lookup for unavailable skills. Adding a diagnostic entry must not silently install optional capabilities or their whole authoring stack.

A reusable case identifies its symptoms and applicable runtime conditions, the checks that distinguish likely causes, the supported correction, and how to verify the original failure. Link normal configuration and lifecycle contracts to their existing owner. Inspect current source and behavioral evidence before promoting an incident into guidance; an observed workaround alone does not establish the cause.

After a verified fix, merge the reusable finding into its existing case, add the missing read-when route, or create a separately selectable case when needed. Keep a small diagnostic table in one reference while it remains sufficient; introduce a `references/troubleshooting/` directory only for distinct tasks. Consolidate matching causes and replace guidance invalidated by the current contract. Preserve version conditions when they materially change diagnosis. Machine paths, temporary commands, dates, and unverified theories remain in project notes or personal knowledge.

When canonical knowledge edits are part of the task, complete the same projection and validation flow as other skill changes. In a consumer workspace, preserve a verified reusable finding for the existing contribution workflow without treating a source lookup binding as upstream write authorization. Website troubleshooting pages project these owners instead of maintaining another case library.

## Maintain existing owners

Use catalog `publication` ownership to locate the capability, then inspect relevant public APIs, examples, and behavioral tests to verify coverage. Catalog module slugs are also legacy website routes, not an exhaustive API inventory. Correct the existing task reference first. Restructure only when actual task use exposes missing guidance, repeated misrouting, or overlapping ownership; do not reorganize files for visual symmetry.

When a topic moves, update incoming Markdown links across skills. Every reference must be reachable from its own entrypoint through relevant local links. Before retiring a website path, check the deployed page and sitemap and preserve published routes with redirects when needed. Monica.Docs uses `SKILL.md` as the landing page and projects references; it does not need a separately authored overview.

Validate links, reachability, entrypoint budgets, and generated projections using the repository commands in `CONTRIBUTING.md`. These checks do not prove semantic ownership or correctness. Walk representative consumer tasks from selection to outcome, checking that only relevant references are needed and each changing contract has one place to update. Keep task-specific review evidence in the change report, not in canonical guidance. See [documentation ownership](docs-information-architecture.md) for publication.
