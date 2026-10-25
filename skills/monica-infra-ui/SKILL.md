---
name: monica-infra-ui
description: Register and use Monica Blazor UI, shell, icons, Markdown, themes, navigation, and localization. Diagnose Blazor script or refresh 404s, empty stylesheets, and inactive pages; use monica-ui-development for components and monica-ui-localization for resources.
---

# Monica Infrastructure UI

Select native UI composition and the task below. Read the owning capability before changing host behavior.

| Task | Read |
| --- | --- |
| Choose hosting, Shell, layouts, pages, or navigation | [Application hosting](references/application-hosting.md), [Shell and access](references/shell-and-access.md) |
| Diagnose scripts, styles, inactive controls, or refresh 404s | [Static assets](references/troubleshooting/static-assets.md), [Routing and refresh](references/troubleshooting/routing-and-refresh.md) |
| Configure icons, themes, languages, or localization | [Icons](references/icons.md), [Theme and localization](references/theme-and-localization.md) |
| Inspect host identity, timing, DbContexts, sessions, or migrations | [System information](references/system-information.md), [Repository diagnostics](../monica-infra-persistence/references/repository-diagnostics.md) |
| Render Markdown, diagrams, document search, or local images | [Markdown](references/markdown.md) |
| Probe connectivity, transform text, or view and edit JSON | [Utilities](references/utilities.md), [JSON](references/json.md) |
| Inspect mapping pairs and expressions | [Mapper workspace](references/mapper.md) |
| Inspect Git workspaces or manage host files | [Git](references/git-workspace.md), [FileOps](references/fileops-workspace.md) |
| Execute commands or inspect sessions, output, and cancellation | [Terminal workspace](references/terminal-workspace.md) |
| Inspect Kubernetes, operate Services, or publish NodePorts | [K8S operations](references/k8s-workspace.md) |
| Compare text or embed a Diff viewer | [Text comparison](references/diff.md) |
| Read and copy captured exceptions or diagnostics | [Diagnostic text](references/stack-trace.md) |
| Theme Swagger or add toolbar destinations | [Swagger workbench](references/swagger-ui.md) |
| Configure timestamp formats and deployment timezone display | [Page time display](references/time-display.md) |
| Center loading or empty regions | [Content states](references/content-states.md) |

Use `$monica-ui-development` for components and `$monica-ui-localization` for translations. Resolve unavailable skills through `$monica-guide` (`source resolve --repository monica --json`), then read canonical `skills/<name>/SKILL.md` and the selected reference.
