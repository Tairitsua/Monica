# CLI and state contract

The engine is the `Monica.Guide` executable (`Monica.Guide.exe` on Windows, `Monica.Guide` on Linux and macOS), exposed as `$monica-guide` once located. See the skill front page for how to locate it (installed executable or `dotnet run --project Monica.Guide.App` inside a canonical checkout). Every command accepts `--json` for the stable machine envelope; this page describes the human-facing surface.

```bash
$monica-guide <command> [options]
```

## Read-only commands

| Command | Behavior |
| --- | --- |
| `overview` | Installed products, versions, detected agent hosts, and next actions. Never requires a workspace. |
| `workspaces` | Every registered workspace with live health: profile, directory presence, installed-versus-profile skill counts, and instruction currency. |
| `status` | Installed skill projections per environment and target, recorded release identity, and host detection. Every recorded tree digest is compared against the configured release catalog, so partially updated or locally modified projections surface as release-drift warnings. Add `--workspace <path>` for repository detection and managed-instruction health. |
| `doctor` | Everything `status` checks (including the release-drift comparison) plus the live loopback runtime probes for products that serve one. Workspace and source-binding checks are appended like every other check. |
| `source list` | Summary check for the Monica framework repository. |
| `source resolve --repository monica` | Observe the stored binding: canonical identity, stored and observed commits, exact ref, provenance, absolute path, dirty/path health, and warnings. |
| `issue status` | The machine-global issue-reporting mode (`prepare`, `ask`, `never`) with its meaning; read it before preparing any issue artifact. |

## Mutating commands

| Command | Behavior |
| --- | --- |
| `configure --workspace <path>` | **The default skill flow**: install the workspace profile's closure into the workspace's project directories (`skillTargets` in `.monica/guide.json`, default `.agents/skills`). `--profile` overrides the closure for this run without rewriting the configuration. Requires a prior `init`; native environments only. Also converges the managed `AGENTS.md` block with the current catalog template and machine state (bound source locators and the issue-reporting policy while their wizard switches are on). |
| `configure` (global) | Explicit machine-wide install into selected environments and targets (`--environment` × `--target shared\|claude`), optionally narrowed by `--profile <name>` or repeated `--skill <name>`. Without environments it refreshes every recorded installation from the running bundle — the release update path, which replays project installations too. Products with a serve port accept `--port`. |
| `unconfigure` | Remove recorded skill projections: `--workspace <path>` for one workspace's installations, `--target`/`--environment` for global ones, or everything when unscoped. A full unconfigure removes the product's ledger entry, locator, and desktop integration. |
| `init --workspace <path>` | Write `.monica/guide.json` (profile, capabilities, optional `skillTargets`) and the managed `AGENTS.md` instruction block, and register the workspace in the engine registry. Requires an explicit `--profile`. Application uses the unified domain/role architecture; add `--capability ui` for business UI. Legacy architecture tags are optional deployment/migration hints. |
| `forget --workspace <path>` | Remove exactly the guide-owned workspace artifacts: the managed block, the Guide-owned `@AGENTS.md` import, `.monica/guide.json`, the project skill installations recorded for that workspace, and its registry entry. |
| `source bind --repository <repo>` | Add or replace one global binding from `--source-path <checkout> [--source-ref <tag\|commit>]`, verified against the local canonical Git checkout. |
| `source unbind --repository <repo>` | Remove that repository's global binding. |
| `issue set --mode <prepare\|ask\|never>` | Set the machine-global issue-reporting mode. An immediate preference save like the wizard's toggle: it is not governed state, takes no plan digest, and never authorizes a remote action. |

Every mutating command is a preview until both `--apply` and `--plan-digest <sha256>` are present. Apply rebuilds the plan from current state under the engine mutation lock and rejects any drift through the digest gate. Exit codes: `0` ready, `1` warnings, `3` errors or blockers, `2` usage or operational failure.

## State model

- **Unified guide ledger** — `<engine root>/state/guide.json` holds one entry per installed product: executable, bundle root, release manifest digest, and every skill installation with per-skill tree digests. Installation identity is the environment plus the normalized target root, so project directories and global catalogs coexist and refresh together. The engine root is `%LOCALAPPDATA%\Monica` on Windows, `~/.local/share/Monica` on Linux, `~/Library/Application Support/Monica` on macOS; `MONICA_GUIDE_DATA_ROOT` overrides it.
- **Workspace registry** — `<engine root>/state/workspaces.json` registers every initialized workspace (path, product, profile, capabilities) so listing surfaces can find them all; the portable truth stays in each workspace's `.monica/guide.json`.
- **Source binding** — `<engine root>/state/source-bindings.json` records the `Tairitsua/Monica` checkout (repository, ref, commit, source path, resolution kind, provenance). It stores no access, dirtiness, or permission state; observations happen at lookup time.
- **Issue-reporting preference** — `<engine root>/state/issue-preferences.json` stores the machine-global mode (`prepare`, `ask`, `never`) shared by every product guide. The mode bounds issue preparation only; a persisted value never authorizes creating an issue, branch, or push.
- **Workspace projection preference** — `<engine root>/state/workspace-projection.json` stores the machine-global switches that project bound source locators and the issue-reporting policy into every managed `AGENTS.md` block. Changes converge with the next workspace initialization or update.
- **Workspace configuration** — `.monica/guide.json` in the repository is shareable and portable: schema version, product id, confirmed profile, capabilities, instruction block version, the Claude-import ownership flag, and the optional `skillTargets` list of workspace-relative skill directories. It never contains absolute paths or timestamps.
- **Release manifest and skill catalog** — the installed bundle root keeps `release-manifest.json` and `skills/catalog.json`; the catalog is the sole authority for which skill directories exist, their digests, profile memberships, managed instruction templates, source repositories, and retired aliases.

Any guide output that includes source bindings or local paths can expose private absolute paths. Keep it local; do not commit or share it by default. Never expose the guide executable or any product's loopback surface beyond localhost.
