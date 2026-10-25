# Monica page design method

This reference covers how to decide what a Monica operational page shows and why. It comes before the [visual language](../../monica-ui-development/references/monica-visual-language.md) paints the page and the [working-interface language](../../monica-ui-development/references/monica-workbench-design-language.md) accepts it. The examples come from the JobScheduler page family (overview, catalog and execution ledger) with placeholder job names.

The target is a page an operator can act from. It answers the route's question first, states precise facts with their population, shows each fact once, and leads from every concern to the record that explains it.

## 1. Learn the domain before the page

Read the module's models, store and facade before drawing. List the rules that change what a number means, and decide where each one appears:

| Domain rule (JobScheduler) | Where it appears |
| --- | --- |
| Retention keeps the newest 100 terminal executions per definition by default, with an optional age limit, so a frequent job's history is pruned within minutes | Window counts say "retained", pruned history is hatched, and the ledger marks from when its records are complete |
| An occurrence that arrives while outstanding executions fill `MaxConcurrency` is recorded as skipped and is not backfilled | Skips get their own lane in the Warning role; the job view explains them when the median duration exceeds the cadence |
| An execution captures retry and timeout from its template at admission | The execution view shows the captured policy, not the current one |
| A policy change replaces the complete override set and rotates the concurrency stamp | The editor saves a complete set and shows declared, override and effective values together |
| An owner's latest definition observation is publication evidence, not a heartbeat | Owner freshness is never painted as process liveness |

Then read the shipped page as evidence, live and read-only when a host is available: what its facade can supply, what it cannot, and which URL parameters, resource keys and dialogs it already owns. Say in one sentence which question it answers and which one it leaves to the operator. The previous scheduler overview answered "how many records exist" with an all-time state donut beside live counts. It could not say which job was failing now, which one was stuck, or what would run next. That gap is the brief.

## 2. One operator question per route

Write each route's question before choosing regions:

- **Overview:** which definitions need attention now, and why? What is running, and is it overrunning? What runs next?
- **Catalog:** find a definition, read its schedule, outcomes and effective policy with provenance, and act on one or several.
- **Execution ledger:** which executions in a time range matter, where do failures concentrate, and what does one execution's evidence say?

Peer questions become sibling routes in one contextual rail. A region that answers another route's question moves to that route or becomes a link to it.

## 3. Define every judgment

A label such as "overrunning" or "needs attention" is a claim. Give it a rule with thresholds, a minimum sample and exclusions where the behavior is expected, and write the rule in the design note so the implementation computes the same one:

- **Overrun:** elapsed time exceeds the P95 of retained completions (twice the median with fewer than 20 samples) and is at least 30 s.
- **Skipping often:** at least 3 skips, and at least 10% of the last hour's retained occurrences. Sub-minute cadences are excluded because skipping overlapped occurrences is their expected throttle.
- **Failing intermittently:** the latest run succeeded, but at least 3, and at least 25%, of the last hour's completions failed.

Show a rate, score, trend or percentile only when the domain defines it over a stated population and window, and show the counts beside it: the catalog's success share is captioned "from 96 succeeded or failed runs". A "reliability" or "health" figure without a formula is decoration.

## 4. The display value and its population

The single display-scale value is the number the operator acts on, with its population in the unit. The overview shows definitions needing attention over present definitions ("7 / 41 present definitions"), the catalog shows present definitions, and the ledger shows the retained records in the selected range. A lifetime total or a success percentage rarely drives an action. Reason counts beneath the value break it down in the same roles as the marks they summarize.

## 5. The finding sentence

Generate one sentence from the data that states a consequence the operator would otherwise have to work out:

- Build it from clauses that each need their own data, and drop a clause when its data is absent. Write a calm variant for when nothing is wrong: "All 41 present definitions are healthy: no latest outcome failed, and nothing is overrunning or skipping often."
- Link every record name to its evidence, and mark each number in the role of its fact: failure counts in Error, elapsed time in Info, an overrun ratio in Warning.
- The strongest findings join facts through a domain rule. The catalog says: "Of the 3 jobs whose latest run failed, 2 run hourly or slower with no retries configured, so they do not run again before their next occurrence." No single fact says that.
- State what the evidence does not cover. The ledger ends: "Records are complete only after 14:05: earlier executions of 3 jobs were removed by retention."

An overview finding reads: "3 jobs failed on their latest run, and InvoiceExport has failed 7 times in a row; OrderSync failed 4 of its 12 runs in the last hour; LedgerRefresh has run for 25:12, 6.1× its median."

## 6. Scope every number

Decide which control scopes which region, and place each control directly above what it scopes. In the ledger, the band with range, owner, job and search scopes the count, the finding, the outcome lanes and the table. The state chips below the finding scope only the lanes and the table, so the display value keeps counting every state. Label scopes when they differ, for example "Retained executions completed in the last 1 h".

Filter option counts exclude their own filter, so each count states what choosing that option would show. A true zero stays visible as an outline in its role. Paused, unpublished and empty are different facts; do not merge them into "inactive".

## 7. Let the evidence's shape choose the signature

Pick the signature from what the data is, then pick encodings that survive its imbalance. The visual language's [component vocabulary](../../monica-ui-development/references/monica-visual-language.md#component-vocabulary) specifies each part.

- **Time per record** (overview): lanes per definition on one host-clock axis. Retained executions sit before the capture instant and the plan projected from the effective cron after it, since capacity can still skip an occurrence. Hatching marks history removed by retention, and a quiet lane pins its latest outcome from before the window.
- **Composition across two real categories** (catalog): a composition matrix of owners by cadence, one mark per definition in the role of its most severe current reason. Every mark is a real record and no quantity is invented; its heads filter the inventory below.
- **Counts over time by state** (ledger): successes far outnumber failures, so a stacked bar would hide the failures. Outcome lanes give each state its own stated peak ("max 34 per bucket"), draw rare active states record by record, and mark where retention leaves the records complete.

Compare the categories' sizes before choosing a shared scale. When a shared scale would flatten the smaller category into a few pixels, give each category its own stated peak, and draw a category with only a handful of records one mark at a time. Give rare marks distinct shapes so color is never the only cue.

## 8. Compose the route

Use one order on every route of a family, so the operator learns it once:

1. The masthead, with the one filled primary action.
2. The contextual rail of sibling routes.
3. The signature surface: the controls that scope it, the display value with its finding, then the signature.
4. The working evidence the route exists for: running work and next arrivals on the overview, the inventory beside the dossier in the catalog, the records table in the ledger.
5. A temporary evidence sheet for one record, opened from any mark, row or link.

## 9. Color by meaning

Write the role map as a table before styling, then compare it with the shipped mapping:

| Role | Scheduler meaning |
| --- | --- |
| Error | Failed outcomes and failing jobs |
| Warning | Skipped occurrences, overrun, a stale snapshot |
| Info | Running work, the live process |
| Success | Succeeded outcomes, the healthy group |
| Primary | Operator admission, selection and the primary action |
| Neutral | Plans, queued work, host context |
| Hatch | History that is not retained |

State roles mark actual states only. Primary belongs to what the operator did or selected, so a manual run reads apart from a scheduled one. The shipped scheduler paints Running in Primary and Queued in Info; the redesign records its different mapping as an open decision instead of changing five routes silently.

## 10. Make the signature operable

The signature is an instrument, not an illustration:

- The keyboard reaches every mark: ↑↓ between lanes and ←→ between executions, a roving-tabindex matrix, ←→ between buckets with Shift for ten, Home and End, Enter to open or zoom, and Escape to clear.
- Hover writes an exact readout into a caption with `aria-live`. Dragging selects a range and a click zooms into a bucket.
- Hover never gates a value: the caption and a table twin carry everything a tooltip would.
- Every view is a link. Filters live in the query with defaults omitted and shipped parameter names kept (`states`, `instanceId`, `present=false`). Filter changes replace the history entry; selecting a record adds one.
- Routes link with context: the catalog dossier opens the ledger filtered to its job, and the ledger's top failing job is a link that filters to it.

## 11. Design every state

List the states before drawing and give each its own treatment:

- An empty scope, no filter match and an empty range are different messages, each with a way out.
- A failed refresh keeps the last snapshot, relabels "now" as "captured" and freezes elapsed times, while plan countdowns keep running.
- An unknown deep-linked selection says so instead of showing empty details; a pruned execution says it no longer exists.
- Long names wrap at meaningful boundaries, such as after each `.` of a namespace, and stay complete for search and copying.
- An action on one route shows on the others, because all routes read the same session.

## 12. Model actions with their real semantics

Model what an action writes, then show the result where the operator looks next. Pause writes the complete override set and resume writes an explicit `false`, as the shipped catalog does. A batch action applies only to eligible records and names how many. The policy editor separates inherit, force enabled and force disabled; it validates minimums, counts unsaved changes and keeps Save disabled until a valid change exists. A confirmation for a disruptive action starts on Cancel.

## 13. Remove what does not answer the question

Each element answers a question the operator has on this route, once. Remove tiles that repeat the display value, all-time shares beside window counts, a chart-type toggle that redraws the same counts, a recent-records list where a ledger exists, and a fact repeated on every row that belongs to its common owner. When two elements answer the same question, keep the one with more context.

## 14. Prove it

Before calling a page done:

- Exercise every control in a browser: filters, selection, keyboard paths, dialogs, focus return, deep links, and back and forward.
- Capture light, dark, English, the top-bar layout, an intermediate width and a phone, plus the detail, stale, empty and deep-link states.
- Measure the contrast of each new text and mark pair in both modes, and measure overflow, alignment and cell insets in the DOM instead of trusting a capture.
- Compare with the before capture and run the [richness review](../../monica-ui-development/references/monica-visual-language.md#richness-review).
- Write the design note described in [prototype practice](aesthetics-guidelines.md#design-note-and-handoff).

## The scheduler family at a glance

| Route | Question | Display value | Signature | The finding joins |
| --- | --- | --- | --- | --- |
| Overview | What needs attention now? | Definitions needing attention / present | Lanes per definition: retained past, projected plan | Latest outcome and streak, the hour's failures, overrun ratio, skips |
| Catalog | What is this definition and how does it behave? | Present definitions | Owner × cadence composition matrix | Ownership concentration; failure, cadence and retries; never-run triggered jobs |
| Execution ledger | Which executions matter in this range? | Retained records in range | Outcome lanes on stated peaks with a completeness boundary | Failures and their top job, skips, running work, retention completeness |
