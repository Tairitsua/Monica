# Profiles and source minimums

Profiles are optional repository initialization presets. Installing or exploring Guide does not select one. `init` confirms a profile; `configure --workspace <path>` installs that profile's closure into the project's configured skill directories (`skillTargets`, default `.agents/skills`).

## Application

Initialize with `--profile application`; the unified domain/role architecture is unconditional. Add `--capability ui` for business UI, or omit it for backend-only. Existing `microservice` and `modular-monolith` tags remain accepted deployment/migration hints and select their migration guidance; they do not choose competing new-project layouts. The managed instruction block routes to `$monica-guide`, `$monica-application`, and the repository-designated project application specification before application work. The repository owns and designates that local specification skill; Guide does not own or install it. No source binding is required.

## Extension author

Extension work consumes Monica through immutable NuGet packages. A Monica `ProjectReference` inside the workspace is an `init` blocker — bind exact Monica source separately as a lookup locator instead, and never add the bound source as a project reference.

## Framework contributor

Requires the workspace to be the canonical `Tairitsua/Monica` checkout at its Git root (origin or upstream identity). The global Monica binding should point at the commit being worked on for exact navigation; it remains lookup-only, and write authorization comes from the current task and active checkout, not from Guide state.

## Detection and versions

The engine infers the candidate profile from canonical Git identity first, then characteristic files (framework markers, package/project references, extension markers, service/domain layout). Detection is advisory and never auto-confirms; ambiguous repositories need an explicit user choice.

Framework versions resolve for display in this order: project-reference root properties, resolved `packages.lock.json`/`obj/project.assets.json` versions, central package management, then project declarations. Mixed versions surface as warnings. When exact source is needed but no verified binding exists, report the missing prerequisite instead of inventing a source.
