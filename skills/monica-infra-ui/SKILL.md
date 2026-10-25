---
name: monica-infra-ui
description: Register and use Monica Blazor UI, shell, icons, Markdown, themes, navigation, and localization. Diagnose Blazor script or refresh 404s, empty stylesheets, and inactive pages; use monica-ui-development for components and monica-ui-localization for resources.
---

# Monica UI hosting

| Task | Read |
| --- | --- |
| Select UI foundation, hosting, Shell, layouts, or application calls | [Application hosting](references/application-hosting.md) |
| Diagnose Blazor script 404, empty CSS, or inactive controls | [Static assets](references/troubleshooting/static-assets.md) |
| Diagnose direct-access or refresh 404s | [Routing and refresh](references/troubleshooting/routing-and-refresh.md) |
| Add shell pages, navigation, or operational access | [Shell and access](references/shell-and-access.md) |
| Render SVG icons in controls or navigation | [MoIcon and icon catalog](references/icons.md) |
| Inspect host identity, process evidence, or timing | [System information](references/system-information.md) |
| Inspect DbContexts, sessions, or migrations | [Repository diagnostics](../monica-infra-persistence/references/repository-diagnostics.md) |
| Render Markdown and diagrams, search documents, or serve local images | [Markdown](references/markdown.md) |
| Probe connectivity or transform escaped text and JSON | [Utilities workspaces](references/utilities.md) |
| Inspect mapping pairs and expressions | [Mapper workspace](references/mapper.md) |
| Inspect Git working copies and credentials | [Git workspace](references/git-workspace.md) |
| Browse, edit, transfer files, or configure FileOps | [FileOps workspace](references/fileops-workspace.md) |
| Execute commands and inspect sessions, output, or cancellation | [Terminal workspace](references/terminal-workspace.md) |
| Inspect Kubernetes, operate Services, or publish NodePorts | [K8S operations](references/k8s-workspace.md) |
| Compare text or embed a Diff viewer | [Text comparison](references/diff.md) |
| Read and copy captured exceptions or diagnostics | [Embedded diagnostic text](references/stack-trace.md) |
| Theme Swagger or add toolbar destinations | [Swagger workbench](references/swagger-ui.md) |
| Set theme/language defaults or localize pages | [Theme and localization](references/theme-and-localization.md) |

Use `$monica-ui-development` for components and `$monica-ui-localization` for translations; configure underlying services through their infrastructure skills.

For unavailable named skills, resolve Monica with `$monica-guide` (`source resolve --repository monica --json`) and read canonical `skills/<name>/SKILL.md` and the specified reference.
