---
name: monica-infra-ui
description: Register and use Monica Blazor UI modules, the shell, icons, Markdown viewer, themes, navigation, and localization in a host. Use monica-ui-development for building components and monica-ui-localization for authoring resources.
---

# Monica UI hosting

UI foundation, hosting, and the optional Shell compose independently; Markdown adds searchable documents.

| Task | Read |
| --- | --- |
| Compose product layouts, routes, and application calls | [Application hosting](references/application-hosting.md) |
| Compose the shell, add feature pages, configure navigation, or authorize operational pages | [Shell and access](references/shell-and-access.md) |
| Render local SVG icons or supply icons to controls and navigation | [MoIcon and icon catalog](references/icons.md) |
| Inspect host identity, process evidence, timing, or operational links | [System information](references/system-information.md) |
| Inspect registered DbContexts, provider sessions, or migration outcomes | [Repository diagnostics](../monica-infra-persistence/references/repository-diagnostics.md) |
| Publish searchable documents, use the viewer, or serve local images | [Markdown](references/markdown.md) |
| Probe connectivity from a host or transform escaped text and JSON | [Utilities workspaces](references/utilities.md) |
| Inspect registered object-mapping pairs and generated expressions | [Mapper workspace](references/mapper.md) |
| Inspect Git working copies and credentials | [Git workspace](references/git-workspace.md) |
| Browse, edit, transfer host files or configure FileOps policy | [FileOps workspace](references/fileops-workspace.md) |
| Execute host-local commands and inspect session, output, history and cancellation evidence | [Terminal workspace](references/terminal-workspace.md) |
| Inspect Kubernetes resources, execute Service-backed operations, or publish the host's NodePort addresses | [K8S operations](references/k8s-workspace.md) |
| Compare text or embed a Diff viewer with source-faithful line evidence | [Text comparison](references/diff.md) |
| Read and copy captured exception or diagnostic text inside an existing UI | [Embedded diagnostic text](references/stack-trace.md) |
| Theme the standalone Swagger reference or add application destinations to its toolbar | [Swagger workbench](references/swagger-ui.md) |
| Set theme/language defaults or integrate custom page localization | [Theme and localization](references/theme-and-localization.md) |

Use `$monica-ui-development` for components and `$monica-ui-localization` for translations; configure underlying services through their infrastructure skills.

For unavailable authoring skills, resolve Monica with `$monica-guide` (`source resolve --repository monica --json`) and read canonical `skills/<name>/SKILL.md`.
