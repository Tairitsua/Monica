# Monica documentation ownership and placement

Canonical Monica Agent Skills and their technical references live in the Monica repository under `skills/<name>/`. Edit them there, then let the repository's projection workflow update installed skill copies and any website-facing skill view. The repository-local `.agents/` and `.claude/` roots are ignored agent state, never committed sources, even when a merge or mirror supplies them. Follow the contributor workflow in the repository's `CONTRIBUTING.md` for local generation, validation, and conflict recovery. A rendered website page is not an independent copy to maintain by hand.

Stable human-facing guides live in a Monica.Docs checkout under `docs/`. Resolve that checkout from the current task or local filesystem; it has no Monica.Guide source binding or docs-contributor profile. Its current navigation and routes decide the final path. The page families are `getting-started/`, `concepts/`, `guides/`, `scenarios/`, `packages/`, and `ecosystem/`, with `en-US/` and `zh-CN/` language trees. Keep paired localized pages at corresponding paths where the site supports alternates.

Document module usage once under the owning `skills/monica-infra-*/references/`. Infrastructure and related operator UI belong together when they serve the same capability. The catalog maps replaced module slugs to their skill owner; the site redirects those old article routes. Scenario guides link to `/skills/<name>` instead of repeating configuration or API contracts.

The release pipeline exports exact canonical Markdown as `monica-knowledge.json` and includes it in `SHA256SUMS`. Monica.Docs verifies the release, framework version, catalog digest, and resource digests before rendering `/skills/`, raw Markdown, and `/llms.txt`. Local previews identify their source commit and dirty state. Edit canonical skills and regenerate their projections; never patch generated site content.

Keep page-specific assets beside their page and shared assets in the site's existing shared-asset location. Link to local Markdown and assets relatively so the site can resolve them.
