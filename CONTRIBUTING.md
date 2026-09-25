# Contributing to Monica

Thanks for helping improve Monica.

Monica is currently preparing the `1.0.0` stable release. During the release-candidate phase, breaking changes are still allowed when they improve the design.

## Development Setup

Prerequisites:

- .NET 10 SDK
- Git
- A C# IDE such as JetBrains Rider or Visual Studio

Restore, build, and run the default test gate:

```bash
dotnet restore Monica.slnx
dotnet build Monica.slnx -c Release -m
powershell -File scripts/run-tests.ps1 -NoBuild   # pwsh -File ... on Linux/macOS
```

`run-tests.ps1` enumerates test projects dynamically and runs them one at a time. Its default gate
covers every test project **except UI (bUnit) projects**: UI tests are not part of standard
verification and run only on explicit request — use `-UiOnly` for just the UI projects, or
`-IncludeUi` for the full suite. CI mirrors this split: `unit-tests.yml` runs the default gate on
every push, `ui-tests.yml` is a manual workflow, and the release gate still runs the full suite.
Test assemblies retain xUnit collection parallelism, while collections that deliberately park
worker threads opt into exclusive execution.

When changing canonical Agent Skills, also run:

```bash
python3 scripts/validate_agent_skills.py
python3 scripts/sync_agent_skills.py --write
python3 scripts/sync_agent_skills.py --check
python3 scripts/test_agent_skills.py
```

Edit Monica-owned skills only under `skills/<name>/`. Their matching `.agents/skills/<name>` and `.claude/skills/<name>` directories are generated projections and must not be edited directly. The generator owns only catalog-managed Monica directories: unrelated external skills, files, and caches in either projection root are preserved and ignored by projection checks. Monica-owned skills use portable `SKILL.md` frontmatter containing only `name` and `description`. Skill release versions live in `.monica/agent-skill-catalog.json` and `.monica/agent-skill-index.json`, not in skill frontmatter.

Nine `monica-infra-*` skills own reusable consumer guidance for persistence, messaging, jobs, configuration, hosting, observability, AI, web, and UI. Keep framework-authoring contracts in their distinct skills.

Follow [reference organization](skills/monica-docs-authoring/references/reference-organization.md): infrastructure entrypoints carry selection knowledge and direct task routing; references own execution guidance. An overview is optional. Update the existing contract owner and affected routes together, and check published URLs before retiring a topic. The validator checks entrypoint context budgets, links, and reachability; source accuracy, task completeness, and nonduplicated ownership require representative consumer-task review.

For an implementation change, check whether the catalog-owned knowledge changed:

```bash
python3 scripts/check_knowledge_impact.py --base <git-revision>
# Or inspect selected files:
python3 scripts/check_knowledge_impact.py --paths <changed-path> [more paths]
```

The checker assigns changed source files to their most specific catalog owner and requires a matching canonical `SKILL.md` or `references/` change. If the implementation change has no effect on reusable usage knowledge, pass `--no-impact "reason"` and include `Knowledge-Impact: none — <concrete reason>` on its own line in the pull request body. The Knowledge Impact workflow enforces this on pull requests. [Monica.Docs](https://monica.dpdns.org/) publishes the release skills from immutable `monica-knowledge.json` bytes and checksum, alongside stable bilingual guides; it does not maintain a second module manual.

## Pull Requests

1. Open an issue or discussion first for broad design changes.
2. Keep changes focused on one feature, fix, or refactor.
3. Update the owning task reference under `skills/monica-infra-*/references/` when public behavior or reusable usage guidance changes. Update its routing, example, or behavioral check as needed in the same change, or explain why the change has no knowledge impact. Monica.Docs keeps stable guides and renders the published skills; do not duplicate module manuals.
4. Keep public XML documentation accurate for developer-facing APIs.
5. Run the relevant build and test commands before opening the pull request.

## Commit Messages

Release notes are generated from commit messages between release tags. Commits that affect package behavior, public APIs, documentation, or migration guidance should use Conventional Commit prefixes:

```text
feat: add module-level metrics
fix: preserve Res<string> facade data
docs: update module registration guide
refactor: simplify JobScheduler state model
feat!: remove obsolete configuration API
```

Use `feat:` for new capabilities, `fix:` for bug fixes, `docs:` for documentation, `refactor:` for behavior-preserving restructuring, `perf:` for performance work, `build:` for build or packaging changes, `ci:` for workflow changes, `test:` for test-only changes, and `chore:` for maintenance. `feature:` is accepted as an alias, but `feat:` is preferred.

Breaking changes must use `!` after the type or a `BREAKING CHANGE:` footer. Direct commits to the release branch are allowed only when they follow the same format or are intentionally non-release maintenance.

Before committing, check whether the diff is breaking from a consumer's point of view. A change is breaking when existing host applications, module authors, package consumers, documented examples, or automation may need to change code, configuration, routes, serialized data, package references, or operational assumptions.

Breaking-change indicators include renamed or removed public APIs, option properties, guide methods, builder extensions, annotations, abstractions, models, modules, facades, configuration keys, endpoints, response shapes, package IDs, documented usage, default behavior, validation rules, exception behavior, persistence formats, service discovery identity, OpenTelemetry resource identity, or Swagger document naming.

Use both forms for clarity when a breaking change exists:

```text
feat!: introduce host-bound Monica composition

BREAKING CHANGE: replace ambient Mo registration with builder.AddMonica(monica => ...); each host now owns its module graph and runtime catalogs.
```

## Coding Standards

- Use English for code comments, XML documentation, and developer-facing annotations.
- Prefer primary constructors for dependency-injected classes with a single constructor.
- Follow the [framework coding contracts](skills/monica-development/references/coding-contracts.md) for public XML documentation, options, registration methods, and C# conventions.
- Facades may return `Res` or `Res<T>`; internal services should use standard .NET return types and exceptions.
- Keep UI colors on MudBlazor palette variables or the approved Monica theme token contract.

## Release Process

Release tags use the `v` prefix:

```bash
git tag v1.0.0-rc.12
git push origin v1.0.0-rc.12
```

The release workflow builds, tests, packs, uploads package artifacts, publishes to NuGet when configured, generates release notes from commit prefixes with `git-cliff`, and creates a GitHub pre-release for `*-rc.*` tags. It also validates the canonical Agent Skills with both the portable Agent Skills validator and Codex validator, installs `monica-guide` from the pushed immutable tag for Codex and Claude Code, verifies the discovered installed directory against the release's exact per-file and per-skill digests, and publishes the catalog, resolved commit, release index, manifest, and skill-tree archive only after discovery succeeds.

The checked-in index cannot contain the commit of the tag that contains it. After the release assets are published, roll the verified index forward in a follow-up commit before advertising the tag or preparing the next release:

```bash
curl --fail --location \
  "https://github.com/Tairitsua/Monica/releases/download/<tag>/agent-skill-index.json" \
  --output .tmp/<tag>-agent-skill-index.json
python3 scripts/roll_forward_agent_skill_index.py \
  --released-index .tmp/<tag>-agent-skill-index.json \
  --expected-tag <tag> \
  --write
python3 scripts/sync_agent_skills.py --write
python3 scripts/validate_agent_skills.py
```

The roll-forward command accepts only additive history from the expected immutable release asset. A later release also downloads the previous tag's verified index and fails if the asset is unavailable, omits history, or rewrites an existing version mapping.

## Security Issues

Do not report security vulnerabilities in public issues. Follow [SECURITY.md](SECURITY.md).
