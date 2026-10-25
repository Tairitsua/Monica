# Changelog

All notable changes to Monica are documented in this file.

This project follows semantic versioning for public NuGet packages. Release candidates may still include breaking changes before the stable `1.0.0` release.

## [Unreleased]

## [1.0.0-rc.15] - 2026-10-17

### Added

- A shared JSON viewer and editor with syntax colors, strict validation, explicit formatting and minification, original-source copying, optional line numbers, and theme-aware presentation across operational UIs.
- Separate state-store provider and data workbenches, plus independently refreshed configuration source and storage pages.
- Unified Monica Guide engine, wizard, and CLI bundles for Windows, Linux, and macOS, with immutable capability knowledge and verified skill installation.
- Shared local SVG icons, Clock-based page time formatting, and consistent diagnostic instruments, content states, and responsive navigation.

### Changed

- JobScheduler now uses owner-local durable scheduling with unified store contracts, operational policy editing, and richer execution evidence.
- Configuration revisions follow semantic state, and one host-owned disclosure setting controls sensitive-value presentation while preserving annotations and write validation.
- ProjectUnit source analysis shares solution loading and compilation snapshots instead of repeating workspace work for individual inspections.
- Module, AI, repository, messaging, and DevOps workbenches use the common Monica visual language and operational access boundary.

### Fixed

- Guide release smoke checks resolve the executable from the bundle manifest instead of assuming an obsolete directory, and retain failed command diagnostics.
- Configuration source inspection retains sensitivity controls when moving into its own page; JSON drafts retain their analysis and apply gates.
- Chat composer sizing, document navigation, empty-state alignment, provider selection, hover feedback, shell scroll containment, and built-in theme scrollbars are consistent across viewport sizes.

### Migration

This candidate includes breaking changes to scheduler ownership, configuration and state-store contracts, operational UI access, page time formatting, and ProjectUnit source analysis. Follow the current owning references under `skills/monica-infra-*/` and the generated release notes before upgrading a consumer; release candidates may change these contracts before `1.0.0` stabilizes.

## [1.0.0-rc.12] - 2026-08-08

### Added

- A schema-validated bilingual Monica Guide bootstrap contract for Codex and Claude Code, with explicit application and extension-author goals.
- Immutable, revisioned module diagnostics snapshots, sanitized baseline exports, lazy assembly inventories, and a five-section Module System observability workbench.
- One-pass structural type discovery with typed stage metrics, bounded startup-work scheduling, optional performance budgets, and causal blocking diagnostics.
- Automatic bounded catalogs for every public module-option property, with attribute or host-policy sensitivity marking and Development-only sensitive-value reveal.
- Immutable `MonicaConfigurationInputPlan` declarations that share one store composition, section-path convention, and ordered managed JSON sources across bootstrap configuration, startup option loading, and runtime module composition.
- `ModuleMcpOption.CreateHttpEndpointPath` as the canonical projection from a configured MCP base route to one logical server endpoint.
- `ProjectUnitSourceAnalysisContract.DiscoverableUnitTypes` as the canonical set of roles the source analyzer can emit.

### Changed

- Monica Guide now treats an explicit immutable tag as authoritative, derives its channel consistently, validates repository and global-release constraints before apply, and diagnoses newer release schemas with an exact reinstall path.
- Agent Skill release metadata now records human-readable per-skill revisions alongside immutable digests; ordinary CI validates revision accounting while only the release workflow publishes revisions and channel pointers.
- Module composition now uses host-bound `ModuleRegistration<TModule, TOptions>` extensions, option-free `Describe(ModuleDescriptor)` graph declarations, startup-frozen options, and `DeclareTypeDiscovery(...)` plans.
- Module identity, dependency ordering, option access, web capability, host requirements, and diagnostics are derived from the compiled host-owned module graph.
- Startup effective-options loading now uses `BuildBootstrapConfiguration(...)` and `EnsureEffectiveOptionsSnapshot[Async](...)`, while `AddConfiguration(inputPlan)` applies the same immutable inputs to the runtime module graph.

### Fixed

- CI now separates parallel compilation from project-serial test execution, while deliberate blocking probes share one exclusive xUnit collection so unrelated tests cannot starve their workers.
- Release history resolution now skips tags that never produced a GitHub release while still requiring the latest published skill index, so a failed candidate cannot truncate release notes or block the next candidate.
- Concurrency stress tests now use test-owned completion signals instead of worker-side timeouts, preventing runner thread-pool starvation from being reported as framework startup-work failures.
- Module startup metrics now publish the tracked application duration deterministically during immediate host shutdown on .NET 10.
- ProjectUnit source analysis contract v3 now matches runtime discovery for direct `ExcludeFromBusinessTypeDiscovery` annotations and non-inherited `Configuration` annotations, preventing excluded or inherited-only source types from leaking into persisted catalogs.

### Removed

- The repository-specific `supervise-subagents` skill; use the host's native collaboration primitives instead of a Monica-owned orchestration wrapper.
- Legacy `Module*Guide` composition objects, the `DiscoverTypes(...)` callback, and fragmented mutable module-inspection contracts.
- Low-level `IMonicaEffectiveOptionsReader`, `MonicaEffectiveOptionsReaderConfiguration`, and `CreateEffectiveOptionsReader(...)` APIs, plus direct module registration for configuration stores or managed JSON sources; declare those inputs through `MonicaConfigurationInputPlan` instead.

## [1.0.0-rc.8] - 2026-08-05

### Changed

- Agent Skill file manifests now normalize repository-relative POSIX paths before applying unsigned UTF-8 ordinal ordering.
- Release workflows now require the tag-derived version, `Directory.Build.props`, and template package versions to match before build and publication.

### Fixed

- Immutable Agent Skill archives independently recompute aggregate and per-skill digests from archived bytes, including prefix-colliding skill names.
- Archive manifests and ZIP entries are emitted and verified in one canonical path order, preventing producer/consumer digest drift.

## [1.0.0-rc.7] - 2026-08-05

### Added

- Canonical Monica Guide distribution with profile selection, diagnostics, transactional installation, and deterministic Codex and Claude Code discovery.
- Immutable Agent Skill release artifacts with per-file digests, per-skill revisions, change origins, and additive release-history validation.
- Multi-package third-party module repository scaffolding with optional OCI provider images and CPU/NVIDIA validation gates.

### Changed

- Agent Skills now use the canonical `skills/monica-*` ownership tree and versioned catalog; legacy compatibility folders and destructive synchronization commands are removed.
- Configuration change-impact analysis now accepts exact definition/path targets, reports parameter-level affected services, and requires UI confirmation before saving.
- Configuration rollback previews are fingerprint-bound and remain stable across concurrent state changes.
- The Kafka management workspace now uses responsive operational surfaces and isolates per-member consumer-rate calculations.
- Release validation treats package IDs case-insensitively and verifies a fresh immutable-tag skill installation before publication.

### Removed

- Superseded repository-owned Monica skill copies and obsolete UI design prototypes.

## [1.0.0-rc.6] - 2026-08-03

### Added

- A first-class third-party module ecosystem standard, compatibility mark, package validator, and scaffolding skill.
- Publisher-owned module keys in the form `<Publisher>.Monica.<Module>[.<Feature>...]`, including case-insensitive collision detection.
- Module-owned UI navigation localization so independent packages can resolve labels from their own resource catalogs.
- Generic Host lifecycle support for hosted-service observability, EventBus auto-discovery, and execution-timing aggregation.
- Checkpointed concurrent module startup work with host-lifecycle barriers and critical-path composition diagnostics.
- Typed ProjectUnit runtime catalogs, status dashboards, and source analysis.
- A unified ProjectUnit execution pipeline and runtime catalog.
- Kafka consumer-group partition metrics and topic throughput snapshots.

### Changed

- `ModuleKey.Create(...)` now validates the publisher-first ecosystem grammar and reserves official `Monica.*` identities for built-in module keys.
- Monica composition now completes at service registration for Generic Hosts and at endpoint mapping for Web hosts, with startup validation for incomplete Web pipelines.
- Hosted-service diagnostics and checkpoints identify runtime instances explicitly, including multiple keyed services of the same concrete type.
- EventBus batch mutations are cancellable and transactional, and auto-discovered subscriptions are owned and cleaned up by the Host lifecycle.
- Configuration activation batches schema analysis and state reads, supports fingerprint-bound rollback, and manages retired definitions.
- Object Mapping uses the current Mapster compiler, supports fail-fast compilation, and caches runtime mappers per scope.
- Kafka administration and consumer diagnostics provide safer native-client handling and responsive operational views.

### Fixed

- Generic Hosts now own and dispose Monica composition state even when they are built but never started.
- Mediator handler activation is preserved after dependency-injection simplification.
- Dapr subscriptions retry transient delivery failures and isolate dead-letter topics per source.
- Markdown viewer resources, AppBar menus, and UI icon geometry follow component and interaction lifecycles correctly.
- Kafka performance sampling avoids native crashes while retaining current per-topic observations.

### Removed

- DynamicProxy-based dependency-injection interception.
- `ModuleExecutionTimingOption.ExposeExecutionTimingEndpoints`; use the shared `EnableMinimalApi` option instead.

## [1.0.0-rc.5] - 2026-07-22

### Added

- Host-bound `builder.AddMonica(monica => ...)` composition with deterministic module-graph validation.
- Stable, Integrations, and Labs package tiers with CI-enforced dependency direction.
- A dedicated `Monica.ProjectUnits` package, official `Monica.Templates`, and the Ordering reference application.
- Host-scoped Object Mapping and ProjectUnits facades for Minimal API and UI consumers.

### Changed

- Logging, localization, JSON serialization, mapping, ProjectUnits, AutoController configuration, DataChannel pipelines, scheduler time zones, and runtime catalogs are owned by each host.
- `Monica.Framework` is a focused application package instead of an all-dependencies bundle.
- RPC client helpers now live in `Monica.WebApi`, alongside the transport feature they configure.
- AutoController exports RPC metadata from the exact compiled producer assembly, skips IDE design-time builds, and writes deterministic producer-specific output.
- `Microsoft.OpenApi`, `System.Security.Cryptography.Xml`, and the SQLite native bundle were upgraded to supported releases with published security fixes.
- Authentication accepts access tokens from the `Authorization` header by default; query-string tokens require an explicit, path-scoped opt-in.
- Configuration persistence and Entity Framework integration now use deterministic, warning-free schemas and read-only serializer options.
- Unhandled-exception responses are safe by default; stack traces and request snapshots now require an explicit trusted-development opt-in.
- Template and reference hosts expose ASP.NET Core health checks instead of hardcoded health payloads.

### Removed

- Ambient `Mo` registration, `builder.UseMonica()`, `Mo.RegisterInstantly(...)`, and process-wide `LogManager` state.
- The unauthenticated JWT decode endpoint and global query-string token extraction.
- Global mutable clock, DataChannel, principal, localization, JSON, mapping, and runtime-environment state.
- AutoController's source-scanning RPC metadata bootstrap; producer assemblies are now the only metadata authority.
- Unused process-wide debug helpers and the unbounded delayed-task scheduler.

## [1.0.0-rc.2] - 2026-05-09

### Added

- Global Monica application defaults, including the root `Mo.ConfigApplication(...)` API.
- Monica endpoint metadata and endpoint port constraints for Monica-owned routes.
- OpenTelemetry metrics dashboards and in-process collector support.
- SignalR send diagnostics with method-level metrics and target drill-downs.
- Terminal and read-only file access capabilities for AI-assisted workflows.
- Monica UI localization skill guidance and validation support.

### Fixed

- Static web asset behavior for NuGet package consumers.
- UI shell setup for static web assets.
- Snapshot summary resource service name and version display.
- SignalR dispatch proxy compatibility by allowing runtime proxy derivation.

## [1.0.0-rc.1] - 2026-05-06

### Added

- First public release candidate for the Monica modular .NET infrastructure framework.
- Typed DDD ProjectUnit conventions for application services, request DTOs, domain services, entities, repositories, domain events, configurations, recurring jobs, and triggered jobs.
- Composable infrastructure modules with the unified `Mo.Add*()` registration pattern.
- JobScheduler modules for recurring jobs, triggered jobs, metadata persistence, execution history, concurrency coordination, and dashboard UI.
- Built-in Blazor operational dashboards on top of `Monica.UI`.
- Configuration, Repository, WebApi, Logging, EventBus, StateStore, AI, Dapr, SignalR, Office, Profiling, and utility module families.
- Source generator packages for framework and AutoController workflows.
- Bundled agent skills under `.claude/skills/` and `.agents/skills/`.
- Bilingual README content and GitHub-hosted demo video reference.

### Notes

- This is a release candidate. APIs may still change before `1.0.0` stable.
- NuGet packages target .NET 10.
- `Monica.Experimental` and example projects are not intended for NuGet publishing.
