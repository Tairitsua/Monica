---
name: monica-docs-authoring
description: Write or revise Monica framework and module guidance, including localized Monica.Docs pages. Use for public API examples, configuration and provider guidance, concepts, and migration guides; keep canonical agent instructions in Monica-owned skills.
---

# Monica Docs Authoring

Choose the artifact by its audience and lifetime:

- **Agent instruction or operational contract:** edit the canonical `skills/<name>/` tree in Monica. Supporting references stay with their skill. The website may project this material, but a web copy is not its source of truth.
- **Stable human guidance:** edit the relevant page in the Monica.Docs `docs/` tree. Use a user-provided checkout, the active workspace, or an ordinary verified local checkout; Monica.Guide does not locate or initialize Monica.Docs.
- **Design proposal, validation log, or transient investigation:** keep it in the owning repository until it becomes durable user guidance. Do not publish a plan as current behavior.

For framework implementation facts, inspect current source. If an exact Monica checkout is needed outside the active repository, `$monica-guide source resolve --repository monica --json` can locate a verified read-only binding. For site structure and editorial conventions, inspect the actual Monica.Docs checkout. Follow [source-of-truth-checklist.md](references/source-of-truth-checklist.md) for module and generated endpoint claims, and [doc-writing-rules.md](references/doc-writing-rules.md) for examples, links, and localization.

Use [reference-organization.md](references/reference-organization.md) when maintaining infrastructure skills: frontmatter supports activation, `SKILL.md` supports selection, task references support execution, and source/tests supply evidence. Keep direct task routing in the entrypoint and one authoritative explanation per contract. An overview is optional; choose reference boundaries by the work they serve. Audit capability coverage against source and behavioral examples, and update incoming links with content moves. Publish module usage through the owning `monica-infra-*` skill, using the catalog's `publication` metadata for source ownership. Use [docs-information-architecture.md](references/docs-information-architecture.md) for stable editorial guides and website projection; do not create another `docs/modules/` manual.

Show the smallest correct registration example in the host's `builder.AddMonica(monica => { ... })` callback. For web hosts, include `app.UseMonica()` and `app.MapMonica()` when the example describes the full host lifecycle. State real option defaults, required feature selections, and provider constraints only after verifying them in code. Request-owned `[ApiEndpoint]` metadata determines generated HTTP behavior; only attributed requests in the published-language namespace become RPC contracts. Internal services and providers explain public behavior but are not themselves the default user API.

Keep English and Simplified Chinese counterparts aligned when the task covers both locales or changes a shared public contract. Write natural prose in each language and preserve API identifiers. Use relative links for local pages and assets.
