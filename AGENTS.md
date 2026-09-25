# Monica

Monica is modular .NET infrastructure for agent-driven development. `Monica.slnx` is the solution; do not introduce `Monica.sln`.

## Working rules

- Code comments and XML documentation in this repository are also English; sibling and consumer repositories own their own conventions.
- Prefer clear boundaries and behavior on the types that own it. Simplify the design instead of accumulating workarounds. Breaking changes are allowed unless the task requires compatibility; keep refactoring within the requested scope.
- Document public and developer-facing contracts, including meaningful option defaults, registration prerequisites, and lifecycle/ownership constraints. Use the [coding contracts](skills/monica-development/references/coding-contracts.md) when changing C# APIs.
- Read [CONTRIBUTING.md](CONTRIBUTING.md) before committing or preparing a release; follow its Conventional Commit policy.

## Task routing

Load the skills relevant to the current work; details belong there rather than in this file.

| Work | Skill |
| --- | --- |
| Framework implementation and module boundaries | `monica-framework`, then `monica-development` or `monica-architecture` |
| Consuming infrastructure in an application | `monica-application` and the matching `monica-infra-*` skill |
| Blazor components, themes, or user-facing strings | `monica-ui-development`; also `monica-ui-localization` when text or resources change |
| Framework tests and shared test infrastructure | `monica-unit-testing` |
| Canonical skills, public guidance, or documentation ownership | `monica-docs-authoring` |
| Guide engine, setup, or release contracts | `monica-guide`; engine changes use its framework-development reference |
| Focused simplification of selected code | `code-simplifier`; architecture redesign uses `monica-architecture` |

For exact dependency behavior, use `inspect-dependency-source` when available and resolve the version actually in use. For uncertain Microsoft APIs, use `microsoft-docs` or `microsoft-code-reference` with official sources. Read dependency versions from project/package metadata instead of copying them into these instructions.

## Verification

For code changes, use the standard gate:

```text
dotnet build Monica.slnx -m -c Release
powershell -File scripts/run-tests.ps1 -NoBuild
```

- Use `pwsh` in place of `powershell` where appropriate. Do not run `dotnet test Monica.slnx`; use the runner or the affected test project.
- Builds must have zero warnings. Resolve warnings rather than suppressing them with `NoWarn` or other suppressions unless explicitly required by the user.
- Avoid concurrent build/test processes that share dependencies or output paths. Use MSBuild parallelism inside one build with `-m`.
- Run or add UI rendering tests only on explicit user request. Use browser smoke checks for UI changes. Requested UI tests protect behavior contracts, not layout, CSS classes, localized-key presence, or visual composition; details are in `monica-unit-testing`.
- For skill or documentation changes, run their validators described in `CONTRIBUTING.md`; do not run unrelated UI tests.

## Knowledge ownership

- Edit Monica skills only in `skills/<name>/`. `.agents/skills/` and `.claude/skills/` are generated projections. Regenerate with `python scripts/sync_agent_skills.py --write` and include canonical and generated changes together.
- Before completing a skill change, run `python scripts/validate_agent_skills.py`, `python scripts/sync_agent_skills.py --check`, and `python scripts/test_agent_skills.py`.
- When implementation changes public behavior or yields verified reusable guidance, update the owning task reference, its routing when needed, and the relevant example or behavioral check in the same change. Follow `monica-docs-authoring` for topic boundaries; replace obsolete guidance rather than appending session notes.
- Run `python scripts/check_knowledge_impact.py --base <base-ref>` for infrastructure changes. If guidance is unaffected, pass `--no-impact "<concrete reason>"` and record that rationale in the PR's Knowledge impact section.
- The catalog at `.monica/agent-skill-catalog.json` owns skill routing, source ownership, and release projections. Monica.Docs owns stable editorial guides and renders published skills; do not create parallel module manuals here.

## Maintaining instructions

Keep this file limited to stable repository-wide rules and task routing. Put implementation procedures in the owning skill, contributor/release workflows in `CONTRIBUTING.md`, and machine-specific setup in personal skills or user-level instructions. Dates, incident narratives, test counts, timings, and migration status belong in change history or memory. Update the existing owner instead of duplicating a rule. `CLAUDE.md` imports this file.
