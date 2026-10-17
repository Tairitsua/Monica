---
name: monica-application-ui-development
description: Implement or refactor Monica business application pages, components, UI state, forms, and application-service calls using the adopted DDD presentation structure. Use with monica-application-ui; reuse shared Blazor and theme mechanics without imposing framework module or Facade architecture.
---

# Monica Application UI Development

Load `$monica-application`, the repository application specification, and `$monica-application-ui` before changing presentation boundaries.

| Task | Read |
| --- | --- |
| Implement pages, state, forms, requests, cancellation, and results | [Feature development](references/feature-development.md) |
| Configure UI services, providers, page assemblies or a custom root | [Application hosting](../monica-infra-ui/references/application-hosting.md) |
| Implement Blazor/MudBlazor mechanics, CSS isolation, icons, or browser storage | `$monica-ui-development` and its focused task references |
| Author resources and translated text | `$monica-ui-localization` |
| Design a visual prototype or audit a concrete interaction risk | `$monica-ui-design` or `$monica-ui-audit`, preserving the application's own layout |
| Verify behavior | `$monica-application-unit-testing`; use browser smoke checks for composition |

Framework module folders and diagnostic Facades are not business UI conventions. Do not introduce modules, new backend layers, or a duplicate frontend DDD model just to implement a page.
