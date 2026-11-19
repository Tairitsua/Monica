---
name: monica-third-party-module-development
description: Create, modernize, validate, package, containerize, or publish independent Monica extension repositories. Use when building one or several publisher-owned Monica NuGet packages, separating capability/provider/UI packages, assigning manifest ecosystem keys and cross-package dependencies, pairing a provider connector with CPU or NVIDIA OCI images, applying ecosystem branding, preparing CI/release automation, or migrating an older third-party module repository.
---

# Monica Third-Party Module Development

Build independent packages and companion images from one explicit repository contract. Treat a NuGet package as a distribution boundary, a Monica module as a runtime capability, and an OCI repository as an independently runnable provider-service boundary.

Resolve every bundled `scripts/`, `references/`, and `assets/` path from this skill's own directory. Do not assume an `.agents` or `.claude` projection path.

## Companion skills by task

- Use `$monica-architecture` for package and module boundaries.
- Use `$monica-development` for host-bound registrations, providers, Facades, and services.
- Use `$monica-ui-development` for UI packages, `$monica-ui-localization` for localization changes, and `$monica-ui-audit` when reviewing a concrete UI risk.
- Use `$monica-unit-testing` for tests.
- Use `$monica-docs-authoring` for canonical Agent Skill instructions or Monica.Docs guidance.
- Use `$monica-ui-bridge-debug` and `$playwright-cli` for runnable UI verification.

Independently published packages override Monica's first-party test naming rule: use one `tests/Test.<PackageId>` project per package. The host lifecycle, isolation, assertion, and test execution rules from `$monica-unit-testing` still apply.

## Workflow

1. Inspect repository instructions, status, license, projects, APIs, tests, and existing release automation. Preserve unrelated work.
2. Collect the repository contract:
   - durable publisher/repository identity and aligned release version
   - every NuGet package ID, project path, description, tags, and package dependency
   - every module name, kind, manifest ecosystem key, full manifest-key dependency, and provider target
   - optional OCI repository, companion connector package, build context, Dockerfile, bake targets, stages, platforms, accelerators, tag suffixes, provider-specific smoke commands, and managed NVIDIA runner labels
   - Monica version, target framework, source visibility, license, branding, contacts, and publishing target
3. Read [ecosystem-standard.md](references/ecosystem-standard.md) and reject invalid package/module identity.
4. Read [project-patterns.md](references/project-patterns.md). Split packages only for real install, dependency, support, or release boundaries.
5. Write schema-v2 `monica.manifest.json` from [manifest-schema.md](references/manifest-schema.md), then run:

   ```bash
   python scripts/scaffold_repository.py --manifest <manifest.json> --output <repository-directory>
   ```

6. Implement the real capability. Do not publish scaffold shells, fake providers, placeholder services, TODO behavior, or sample secrets. OCI declarations generate the deterministic Bake contract and directory only; implement every declared Dockerfile before validation.
7. Add sociable tests at the smallest honest boundary. Prove package references, module composition, provider selection, localization, and UI integration through real host composition.
8. Validate source, package metadata, and optional OCI targets:

   ```bash
   python scripts/validate_repository.py --root <repository-directory>
   python scripts/validate_localization.py --root <repository-directory> --strict  # UI only
   python scripts/validate_oci.py --root <repository-directory>                   # OCI only
   ```

9. Restore the exact declared Monica NuGet version, then build, test, and pack sequentially using paths understood by the selected SDK. Do not replace Monica package references with local source-project switches. The repository validator rejects any `Monica.*` `PackageReference` whose resolved version differs from `monicaVersion`.
10. Inspect the exact release artifact set and consume every package entry point from a clean local feed:

   ```bash
   python scripts/inspect_packages.py --root . --artifacts artifacts
   python scripts/inspect_images.py --root .  # after local image builds
   ```

11. For NVIDIA targets, run an actual `docker run --gpus ...` recognition smoke test. Building a CUDA-tagged image or running `nvidia-smi` alone does not prove provider execution.
12. Read [publishing.md](references/publishing.md), validate an immutable effective version, and publish only after all NuGet and OCI artifacts pass together.

## Repository and dependency rules

- Use manifest `schemaVersion: 2`; keep package ecosystem tags at `monica-ecosystem-v1`.
- Give every package exactly one packable project at `src/<PackageId>/<PackageId>.csproj`.
- Keep `PackageId`, project name, assembly name, and root namespace identical.
- Declare the NuGet graph through package `packageDependencies` using full package IDs.
- Preserve declared package/module casing in the repository contract. The scaffold resolves
  case-insensitive dependency input to the owning declaration before emitting Linux-sensitive paths.
- Declare the repository distribution graph through module `dependsOn` using full manifest ecosystem keys. The scaffold resolves each key to a concrete `Require<TModule>()` call in `Describe(ModuleDescriptor)`. Runtime identity is the module `Type`.
- Back every cross-package module dependency with a package dependency.
- Use `kind: provider` plus `providerFor` for a module implementing `IModuleProvider`; include the target manifest key in `dependsOn`, return the target CLR type from `ProvidesFor`, and declare the hard runtime edge in `Describe`.
- Never embed a sibling package assembly to avoid a dependency. The packed-artifact inspector rejects this.
- Keep one aligned manifest version for all NuGet and OCI artifacts in a repository release.

## Identity and navigation

- Reserve `Monica.*` and the purple Monica logo for first-party packages.
- Name third-party packages `<Publisher>.Monica.<Package>[.<Variant>]` with dot-separated ASCII letter-or-digit segments and at most 100 characters.
- Keep all packages in one repository under the same publisher segment.
- Put package-owned module types and registration extensions in `<PackageId>.Modules`. Cross-package dependencies use fully qualified concrete module types and qualify option types when an option contribution is needed. Monica-owned modules live in `Monica.Modules`.
- Make every manifest module key equal its owning package ID or start with `<PackageId>.`.
- Use a final `.UI` package/key segment for a separately distributed UI package.
- Derive a UI category ID by removing only the UI module's manifest ecosystem key final `.UI` segment. Implement `IUIModule`; do not infer UI runtime identity from the key suffix.
- Contribute each UI category and its pages through one `ModuleShellUIOption.ConfigureNavigation` callback in `Describe`, and contribute the module-owned resource marker to `ModuleLocalizationOption` there as well.
- Derive public routes from the package family without `<Publisher>.Monica.` or a distribution-only final `.UI`: `Tairitsua.Monica.GachaPool` uses `/gacha-pool`; `Tairitsua.Monica.AI.OCR.UI` uses `/ai-ocr`.
- Keep public routes under the package-family prefix; the host route namespace is shared and duplicate normalized routes fail fast.

## OCI contract

- Model one OCI repository once and list its CPU/GPU variants as bake targets; do not create a second repository merely for acceleration.
- Make every OCI image name one connector package that owns a `kind: provider` module through `companionPackageId`.
- Keep context, Dockerfile, runtime stage, platform, accelerator, and tag suffix explicit.
- Derive immutable tags as `<manifest-version>-<tag-suffix>`.
- Use one Dockerfile DAG so targets share base, dependency, application, and model layers before diverging into CPU/NVIDIA runtime stages.
- Pin base images and dependency/model inputs. Run as non-root, declare a health check, and emit OCI version/source/revision plus Monica companion-package/accelerator labels.
- Make NVIDIA images fail fast when GPU execution is requested but unavailable unless the product explicitly documents CPU fallback.
- Add `releaseGates` only after provider-specific CPU/NVIDIA smoke scripts exist. Every CPU target requires `cpuSmokeCommand`; every NVIDIA target requires `nvidiaSmokeCommand` plus shared `managedNvidiaRunnerLabels` containing `self-hosted` and `nvidia`.
- Treat missing release gates as fail-closed: the scaffold omits the entire publish workflow for a release unit containing OCI images. With complete gates, it loads and inspects every image and runs meaningful CPU/NVIDIA provider inference before registry login or any artifact push.

## Branding and licensing

- Copy the canonical emerald compatibility assets unchanged or use a publisher-owned icon.
- As optional open-source README presentation guidance, prefer a centered project logo at the top, a centered badge row immediately beneath it, and then a centered language selector before the introduction; keep this ordering consistent across localized READMEs. For UI-bearing packages, also consider representative desktop and narrow-width demo screenshots near the overview.
- Add the open-source badge only when `license.openSource=true` and a NuGet SPDX expression is declared.
- Include the compatibility self-attestation and independence notice when using the mark.
- Never claim official, certified, verified, endorsed, or supported status.
- Let the publisher choose open-source, proprietary, dual, free, or paid distribution. Read [licensing-and-commercial-use.md](references/licensing-and-commercial-use.md) for nonstandard licensing or restricted feeds.

## Completion gate

Do not describe a repository as ready until all applicable checks pass:

- manifest-to-project and project-to-package bijection
- package, assembly, namespace, manifest-key, concrete module `Type`, module/provider, and dependency-graph alignment
- exact manifest-declared Monica NuGet restore with no version drift or local source override
- XML documentation and zero warnings for restore/build/test/pack
- synchronized `zh-CN`/`en-US`, theme-token, responsive, bridge, and browser checks for UI packages
- exact `.nupkg`/`.snupkg` set, internal nuspec dependencies, README/icon/license/symbol metadata, and clean consumers
- no sibling assemblies, secrets, machine paths, or source-project references in packages
- exact Bake targets and `group.default`, image platform/labels, non-root user, health check, CPU smoke, and real GPU OCR smoke for OCI releases
- prerelease extension version whenever any Monica dependency is prerelease
