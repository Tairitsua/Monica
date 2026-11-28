# Replicate a prototype without accumulating parallel UI code

Use this workflow when implementing an accepted prototype or consolidating repeated visual patterns after implementation. The prototype establishes the intended hierarchy, density, grouping and interaction; its incidental HTML markup and browser chrome are not the component specification. Current source establishes available evidence and behavior. Resolve conflicts explicitly instead of inventing data to imitate a screenshot. The [working-interface language](monica-workbench-design-language.md) owns visual acceptance and containment; this reference owns asset selection and consolidation.

## Map the accepted design to owners

Before coding, inspect the accepted route, relevant states, native page and state owner. Record a small task-local mapping: prototype element, existing component or owner, required behavior, and any real capability gap. Consult the [shared component selection guide](../../monica-infra-ui/references/visual-components.md) first, then search current source for the candidate and its callers. A catalog entry is a starting point, not proof that the component's contract fits.

Classify each difference:

- **Geometry or paint:** measure the prototype and rendered native element at the same owner width, locale, theme and state. Correct the existing presentation owner.
- **Prototype placeholder or defect:** replace unstyled browser controls with existing Monica or MudBlazor controls. Fix obvious rendering defects during replication, including clipped selection or focus outlines, unintended centering and unequal insets. Preserve the intended information structure and explain material corrections in the change report.
- **Reusable semantics:** extend a shared component when several real callers need the same contract, then migrate them in the same change.
- **Business composition:** keep module-specific evidence, data access, state transitions and aggregate calculations with the module. Compose shared primitives around it.
- **Unavailable evidence or behavior:** verify the backend contract and extend that boundary only when the task requires it. Do not fabricate successful states or complete history to fill the design.

Keep the mapping and screenshots in the task's temporary evidence location. Put only verified reusable contracts in canonical skills; do not append incident narratives or machine-specific preview URLs.

Use the existing themed selection and date/time components, such as `MudSelect`, `MudDatePicker` and `MudTimePicker`, when a prototype uses a plain `select` or browser `date`, `time` or `datetime-local` input as a functional placeholder. A matching closed field does not justify an unthemed operating-system popup. Preserve the field's value and null semantics, precision (including seconds when required), timezone interpretation, validation and change notifications. Ordinary styled buttons, links and simple search fields can remain native HTML when their semantics fit; this is not a requirement to wrap every element in a component.

## Extract, migrate, and delete

An extraction is complete when the common owner is used and the replaced implementations are gone. Search for old class names, local render helpers, duplicated declarations and sibling variants. Remove unused styles instead of leaving the old implementation behind a new wrapper. Prefer a narrow parameter expressing meaning over page-name switches, arbitrary style dictionaries, or flags for every screenshot difference.

Inline findings are a useful example: `MoValueMark` owns emphasis and linked-subject treatment, `MoRichFinding` owns localized numbered slots, resources own sentence order, and the page owns what the evidence means. Status labels, state filters and command buttons remain separate because their semantics and keyboard behavior differ.

Read every existing caller before changing a shared default. Deliberately plain text should become ordinary text rather than consuming an emphasized component and stripping its paint with `::deep`. Preserve intentional variations through a justified shared contract only when they are reusable. Do not let a page override shared component internals to recover its previous appearance.

## Verify appearance and behavior separately

Use the repository's build and test gates, plus browser smoke checks. Follow the existing test policy: a visual extraction does not justify UI rendering tests for CSS classes, localized-key presence, or markup composition. Non-rendering behavior tests can protect a new parser or formatter contract when needed.

Exercise the changed component in representative real callers, including another module when the shared default changes. Check zero values, long identities, reordered localized fragments, navigation and keyboard focus. Compare equivalent light/dark and narrow/intermediate/wide states at the actual component breakpoints. Inspect the applied styles and screenshots; a correct class name, successful build or no-overflow result cannot prove fidelity. Preserve the accepted design's content insets, marker sizes, font weight, and selected-control paint.

Measure the page root against its shell content owner at a viewport wide enough to expose inherited limits. Trace width constraints through shared page classes and the shell. Operational workbenches fill the available owner after the designed gutters. Limit individual reading columns or prose locally when line length needs a bound.

Compare the whole control group at matched owner widths, then inspect the painted inner controls. Record field heights, text weight, border contrast, gaps and alignment against neighboring elements. A compact dropdown can retain its native input margin or minimum height after its wrapper has been sized. Use the visible localized option text for intrinsic width, cap it at the group's available width, and check a long option with adjacent metadata. In a ledger, verify the counter's anchor in the heading and the alignment of sort, direction and grouping when the toolbar is on one row and when it wraps. Include the dark-mode border and surface paint in the paired captures.

Build a small state check for each changed control. Compare the actual paint at rest, hover, keyboard focus, press and disabled states. For a compound search, verify the empty field, a draft value, an applied value and clearing. Place Clear inside the field ahead of an explicit Apply action. A field-level clear resets the keyword, while the separate all-filters action resets its own broader scope.

When a row sits flush inside a rounded list, match the selection and focus paint to the list's inner radius, which accounts for its border width. Inspect the painted corners on the first, last and only rows. Scroll a selected row to the visible edge and check the result in both themes.

Compare the visible shape and optical center of each colored marker against its adjacent text, including rows with mixed font sizes and wrapped labels. Reuse one nominal size for markers with the same role within a group. Preserve meaningful shapes such as severity diamonds. If the prototype marker sits low against the text or the group no longer aligns with a wide heading, correct the alignment in the shared marker or its owning layout. Check the group again after reflow.

Record paired captures of the populated inventory including its last row, the compact record arrangement, the opened inspector at the top and bottom, and the decision actions. Compare field placement, row height, label/value order and available commands before tuning paint. Inspect combinations of independent state signals in the same cell. For example, an active database session keeps its info-colored state pill while its lock wait uses a separate error-colored line. Include the ordinary, waiting and unknown cases in the comparison. Choose the reflow from the component's usable width, then verify actual columns and cell formatting at that boundary.

For a portaled inspector, inspect the generated portal root and measure the title, status marks, stationary actions, each tab and the first body section against the sheet edge. Verify that related marks sit with the subject, commands remain in their designed stationary slots, and only the body scrolls. `TitleContent` owns the subject identity, while the shared side-sheet contract defines the inset for the toolbar and tabs. Switch tabs after scrolling a long panel down and verify the new panel's initial scroll position. A native element with `display: contents` has no box and cannot own scrolling. Keep measured differences and captures of the exact states with the task-local evidence. Recheck each annotated region in the theme, language and viewport where the discrepancy was reported before claiming fidelity.

Open every changed picker, select and menu during browser review. Check its theme, localized calendar and control text, viewport containment, keyboard selection/dismissal and restored focus. Exercise clearing, invalid input and accepted changes; verify that a date/time selection retains required seconds and uses the declared timezone rather than the browser's timezone. Correct an obvious prototype alignment defect instead of turning it into an acceptance target.

## Feed verified results back into the skill route

Keep the executable asset in its production owner and its usage guidance in the corresponding `monica-infra-*` reference. Add or update a read-when route when an agent would otherwise miss it. Authoring procedure belongs here; recurring visual failure checks belong in `monica-ui-audit`; palette grammar belongs in the shared visual language. Update the existing explanation rather than creating a second component manual or a new skill for each primitive.

For compact registry controls, exercise a real copy from a focused evidence sheet and dismiss the sheet with Escape directly afterward. Verify that an empty filter hides its pager, a single page follows the page's range policy, and moving past the first page preserves the owning URL or local index. Check patterned status dots in both themes and languages at phone width.

After a verified extraction or repeated defect, complete the same change with the relevant usage example, selection boundary and regression check. Run the canonical skill validators and projection workflow described in `CONTRIBUTING.md`, and run `scripts/check_knowledge_impact.py --base <base-ref>`. Review representative tasks from entrypoint to component use; mechanical checks do not establish that the prose describes the right behavior.

The feedback loop has distinct responsibilities: agents maintain the source-backed guidance; the catalog maps implementation paths to knowledge owners; Knowledge Impact CI detects missing owner updates on pull requests; skill validators check structure, links and reachability; projection and release tooling distribute the canonical files. These checks do not write new guidance, inspect screenshots, or establish semantic correctness automatically. Keep that review explicit in the change rather than describing the pipeline as autonomous skill learning.
