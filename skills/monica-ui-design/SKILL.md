---
name: monica-ui-design
description: Design or prototype a Monica UI before or alongside Blazor implementation. Use for page and page-family redesigns, new module pages in an existing prototype workspace, interaction flows, visual direction, design notes, and implementation handoff.
---

# Monica UI Design

Design from the domain's real evidence. Before drawing, learn what the module's states, policies, retention and observations mean in source, read the shipped page for what it answers and what it leaves unanswered, and write the operator's question for each route. Every number, mark and sentence on the page traces back to that evidence.

| Task | Read |
| --- | --- |
| Decide what a page shows: questions, judgments, display value, finding, scope, signature, states, actions | [Design method](references/design-method.md) |
| Build and verify the prototype: workspace, fixtures, interactions, reflow, checks, design note, handoff | [Prototype practice](references/aesthetics-guidelines.md) |
| Design an inspector or drawer using the existing shared implementation | [Inspectors and drawers](references/aesthetics-guidelines.md#inspectors-and-drawers) |
| Signature region, color role map, scale tiers and shared component vocabulary | [Visual language](../monica-ui-development/references/monica-visual-language.md) |
| Evidence honesty, containment, scroll ownership, interaction state and page acceptance | [Working-interface language](../monica-ui-development/references/monica-workbench-design-language.md) |
| Theme paint, fonts and shell geometry | [Precision Default](../monica-ui-development/references/monica-precision-design-language.md) or [Grove](../monica-ui-development/references/monica-grove-design-language.md) |

Every page gets a signature region built from real evidence, a multi-role color map and clear scale contrast. A neutral page with one accent is a defect, not a style, and so is a page that fills space with tiles, rates or charts the domain does not define. A page redesign does not replace the preserved Default palette; a prototype's paint stays a proposal until a theme contract adopts it.

Produce the artifact the task needs: a design note, a wireframe, an interactive prototype or an implementation-ready page specification. When the repository already has a prototype workspace under `.ui-design/`, add pages to it and follow its README and agent guide, reusing its registry, shared vocabulary, page-family session and fixtures; otherwise use `.ui-design/{feature}/`. If the user also requested implementation, continue with `$monica-ui-development` in the same task. A design direction does not require a separate approval ceremony unless a consequential choice remains unresolved.
