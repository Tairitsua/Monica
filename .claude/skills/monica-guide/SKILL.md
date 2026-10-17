---
name: monica-guide
description: Explain, bootstrap, configure, diagnose, update, and route the Monica toolbox, including application specification loading, extension/framework work, skill installation, and verified source lookup.
---

# Monica Guide

The authoritative guide engine is the **Monica.Guide executable** (`Monica.Guide.exe` on Windows, `Monica.Guide` on Linux and macOS); this skill only teaches and routes. Locate the executable and assign it to `$monica-guide`, then use its `overview | status | doctor | configure | unconfigure | init | forget | source <action> | issue <action>` commands as the single source of truth for installation, workspace bootstrap, source lookup state, and the issue-reporting mode.

When changing the Guide implementation, use [framework-development.md](references/framework-development.md) for engine, product, and catalog ownership.

## Locating the engine

Try in order:

1. The installed guide executable: read `ExecutablePath` from `%LOCALAPPDATA%\Monica\installation.json` (Windows; `~/.local/share/Monica` on Linux, `~/Library/Application Support/Monica` on macOS). `MONICA_GUIDE_DATA_ROOT` overrides the data root.
2. Inside a canonical Monica checkout: `dotnet run --project Monica.Guide.App -- <command> …` (development form; commands and envelopes are identical).

Every mutating command is preview-first: it prints a plan with a `planDigest`, changes nothing, and only `--apply --plan-digest <digest>` executes the unchanged approved plan. Exit codes are stable machine contracts: `0` ready, `1` warnings, `2` usage error, `3` errors/blockers. Prefer `--json` for machine parsing.

## Workflow

Monica skills install **into the project by default**, never globally unless the user explicitly asks for a machine-wide install. The one built-in exception is the guide skill itself: the product's `GlobalGuideSkill` preference (default on, persisted in `%LOCALAPPDATA%\<product>\configuration\preferences.json`) keeps the guide skill global-first, so workspace closures exclude it and a workspace update removes its stale local copy instead of reinstalling one per workspace.

1. Start without assuming a repository or development path:

   ```bash
   $monica-guide overview --json
   $monica-guide workspaces --json
   ```

   Explain the installed products, registered workspaces, and reasonable next actions. Ask what the user wants to do. Never force initialization on an empty, mixed, or non-Monica directory.

2. When an agent needs first-party source, resolve the global binding before guessing a path, cloning, or opening a moving branch:

   ```bash
   $monica-guide source resolve --repository monica --json
   ```

   Treat a returned path as a verified lookup location, never as write authorization. Report dirty-checkout and commit-movement warnings instead of hiding the path. Bind or rebind with `source bind --repository <repo> --source-path <checkout>` (optionally `--source-ref <tag|commit>` to require an exact ref), always through preview and apply.

   Before preparing any upstream issue artifact, read the machine's issue-reporting mode with `issue status` and honor it: `prepare` allows local drafts (remote actions still need current-session approval), `ask` requires asking before drafting, and `never` forbids issue preparation entirely. A persisted mode is never authorization for a remote action.

3. Initialize a repository workspace after the user confirms a concrete profile:

   ```bash
   $monica-guide init --workspace <path> --profile <application|extension-author|framework-contributor> --capability <microservice|modular-monolith|ui> --json
   ```

   `init` writes `.monica/guide.json` (including the optional `skillTargets` list of workspace-relative install directories, default `.agents/skills`) plus one managed instruction block in the root `AGENTS.md` (and the minimal `@AGENTS.md` import in `CLAUDE.md` when a Claude target is installed), and registers the workspace in the engine registry. The managed block also projects this machine's verified first-party source locators and the issue-reporting policy while the wizard's projection switches are on; a workspace update converges those sections with the same preview and apply. Inspect advisory detection with `status --workspace <path>` first; detection is a suggestion, confirmation is the user's.

   For `application`, choose backend architecture independently of UI. Without `--capability ui` the workspace is backend-only. Add `--capability ui` to include the application UI specification, development skills and shared UI mechanics. The wizard exposes the same Backend only / With application UI choice. One project application spec owns both sides.

4. Install or refresh that workspace's skills — this is the default skill flow:

   ```bash
   $monica-guide configure --workspace <path> --json
   $monica-guide configure --workspace <path> --apply --plan-digest <digest>
   ```

   The workspace's confirmed profile and selected capabilities select the skill closure from the release bundle's catalog; skills land in the workspace's configured project directories. `unconfigure --workspace <path>` removes exactly that workspace's installations; `forget --workspace <path>` removes the configuration, instruction block, project skill trees, and registry entry together.

5. Global installs are explicit opt-ins only (`configure --environment windows --target shared|claude`), for users who want the skills in every project; never choose them on the user's behalf. The guide skill is the exception already covered above: it rides the global-first preference, and disabling that preference restores the per-workspace copy on the next update.

6. Diagnose with `status` or `doctor` (add `--workspace` for repository-specific checks, including the installed-versus-profile skill count). The wizard runs the same diagnosis at startup in the background and promotes any skill-projection or workspace staleness finding to a warning banner on every page: one chip per drifted target — a global catalog (shared/claude) or a workspace path — with the check messages naming the drifted skills in each chip's tooltip, cleared by a refresh once the projections reconverge. Update skills by configuring a newer release bundle — or with the wizard's Update page, which verifies the release `SHA256SUMS` before activation. The wizard also offers a Workspaces page listing every registered workspace and a Sources page observing the global source bindings and the issue-reporting mode.

7. Before application design, implementation, refactoring, or review, load `$monica-application` and the repository-designated project specification, then the affected architecture/unit references. Follow [specification evolution](../monica-application/references/01-specification-evolution.md) for a gap or new pattern. Route framework and extension work to `$monica-framework`, documentation work to `$monica-docs-authoring`, and upstream contribution preparation to `$monica-contribution`.

## Operating rules

- One active skill release per product, recorded in the unified guide ledger; installations are keyed by environment and target root, so many project directories coexist with the global catalogs, and each is removable through its own `unconfigure` preview.
- Maintain at most one global binding per first-party repository. Bindings record exact provenance (ref, commit, resolver) and remain lookup-only locators; never treat them as permission for remote or repository mutations.
- Keep `stable`-grade releases immutable: never replace an unavailable release asset with a branch or another version. Offline operation uses the installed bundle and local bindings only.
- Install downstream Monica skills only for a user-selected profile, into that project's directories by default; require explicit approval before replacing an active installation.
- Source public bootstrap prompts only from [assets/bootstrap-prompts.json](assets/bootstrap-prompts.json). Never maintain host-, goal-, release-, or locale-specific executable prompt copies outside that manifest.
- Extension work must consume Monica through immutable NuGet packages; a Monica `ProjectReference` in an extension workspace is an `init` blocker. Bind exact source separately as a lookup locator instead.

Read [references/toolbox.md](references/toolbox.md) for intent selection, [references/cli.md](references/cli.md) for the command and state contracts, [references/profiles.md](references/profiles.md) for profile source minimums, [references/versioning.md](references/versioning.md) for release timing, and [references/safety.md](references/safety.md) for discovery, offline, privacy, and contribution constraints.
