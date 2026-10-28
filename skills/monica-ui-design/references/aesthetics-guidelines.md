# Monica prototype practice

This reference covers building and verifying a browser prototype once the [design method](design-method.md) has settled what each route shows. Composition and paint follow the [visual language](../../monica-ui-development/references/monica-visual-language.md), acceptance follows the [working-interface language](../../monica-ui-development/references/monica-workbench-design-language.md), and fonts and palettes come from [Precision Default](../../monica-ui-development/references/monica-precision-design-language.md) or [Grove](../../monica-ui-development/references/monica-grove-design-language.md). Do not maintain another palette or page template here.

## Workspace

- Extend the repository's prototype workspace when one exists. Read its README and agent guide, register each route in its page registry, and compose its shell, tokens and shared vocabulary instead of restyling local copies. Page stylesheets own composition only.
- Give a page family one session that every route reads, holding a single snapshot and the family's actions, so an action on one route shows on the others. Share its masthead, contextual rail, stale notice, empty state and evidence sheet. Each route still owns its refresh policy and failure state, as each Blazor page owns its state.
- Split modules by concern: a model that derives every presented fact and judgment without rendering, the signature instrument, the evidence sheet, the messages, and the page that composes them. Start each module with a comment naming the Blazor part or facade query it stands for; start the page with its role map.
- When several designers share the workspace, keep a page's code, styles and fixtures inside its own folders and add only registry entries and smoke states elsewhere. Propose a shared part in the design note instead of editing files other pages depend on.
- Without a workspace, use `.ui-design/{feature}/`. A small prototype may be one `index.html`; split CSS, JavaScript and fixture data when it helps review.
- When several directions are requested, vary the information hierarchy and interaction, not the accent color. The JobScheduler family in the design method is a worked example; learn its grammar rather than copying its regions onto unrelated tasks.

## Fixtures

- Model the fixture on the real scope, captured read-only from a running host or its store: real cardinalities, name lengths, cadences and state mixes, and the problems operators actually have, such as a failing cleanup job, a stuck execution or a job that skips because it outruns its cadence.
- Make it deterministic. Derive history from a fixed anchor so each refresh extends the same history with stable identifiers, and replay prototype actions such as run now, pause, cancel and policy saves into later snapshots.
- Record provenance in the design note: what is real, what is synthetic and what is derived. Sanitize scope keys, hosts and identifiers before the fixture leaves the team, and never present fixture data as live host evidence.
- Apply the [prototype time-display contract](../../monica-infra-ui/references/time-display.md#prototype-time-display) to timestamp fixtures, labels and timezone verification.
- Model only data the facade supplies or that a named, bounded query could supply. Record each missing query as an open decision instead of hiding it behind a decorative control.
- Expose review scenarios for the states the design must survive, such as an empty scope, a failed refresh and an unknown deep link.

## Interactions

- Make the primary interactions work: filters change results with honest counts, selection opens the matching evidence, dialogs validate and confirm, and close, Escape and focus return work.
- Keep view state in the URL. A filter change replaces the history entry and a selection adds one; defaults are omitted and shipped parameter names keep working.
- Model each action's real write and show its result where the operator looks next, as described under [actions](design-method.md#12-model-actions-with-their-real-semantics).
- Give an overlay an accessible name, initial focus, focus containment while open and focus return on close, following the [interaction contract](../../monica-ui-development/references/monica-workbench-design-language.md#preserve-interaction-and-runtime-state).
- A prototype may approximate a primitive's paint, but its handoff names the MudBlazor control and the keyboard and focus behavior to verify on the installed version.

## Reflow

- Reflow from the page's own width with container queries; the shell's navigation changes the width a page receives at the same viewport.
- Check a wide desktop, an intermediate width with the sidebar open (about 930px) and a phone (390px). Tables become labelled rows on phones, a matrix becomes labelled lines, and numeric columns stay end-aligned at wide widths.
- Measure in the DOM instead of trusting a capture: `scrollWidth` against `clientWidth` at each owner, header and cell alignment, and text insets against the painted surface.

## Color, type and assets

- Map every color to a palette or approved token role and every font to the theme's interface, display or diagnostic role. Name prototype tokens after the `--mud-palette-*` and `--mo-*` variables they stand for.
- Measure each new text and mark pair in both modes. When a pair fails, adjust its ink mix and keep the accepted fill.
- Keep assets local, vendoring libraries with their licenses and fonts as WOFF2, so the prototype runs offline.

## Verification

- Run the workspace's smoke check over every state of the page: light, dark, English, the top-bar layout, the intermediate width, the phone, the evidence sheet, and the stale, empty and deep-link scenarios. Expect no browser errors and no unintended overflow.
- Exercise each interaction in a real browser, including keyboard paths and back and forward.
- Look at the captures yourself, cropping to inspect edges and small text. A capture shows composition; DOM metrics prove containment.
- Compare with the before capture and run the [richness review](../../monica-ui-development/references/monica-visual-language.md#richness-review).

## Design note and handoff

Write one `design.md` per page family beside its code:

1. **Task and change:** each route's question, and what the previous page failed to answer, linked to its before capture.
2. **Composition:** the regions in order and what each answers.
3. **Roles and scale:** the role map and scale tiers as tables.
4. **Definitions:** every judgment, threshold, scope note and refresh behavior the page relies on.
5. **Implementation gaps and open decisions:** each missing facade query, each deviation from shipped behavior such as defaults, parameters or color mapping, and each assumption.
6. **Handoff:** each prototype part mapped to its MudBlazor control or shared Monica component, and the shipped resource keys it reuses.
7. **Fixture:** its provenance.
8. **Verification:** the states captured, the contrast measured and the interactions exercised.

Keep the before and after captures beside it. During implementation, compare the browser implementation with the prototype's hierarchy, readable evidence, spacing and surface roles before judging colors alone.
