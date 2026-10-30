---
name: monica-architecture
description: Design or review Monica framework module boundaries, public and internal layers, provider placement, feature folders, module kinds, and Blazor page decomposition. Use for framework structure rather than consumer setup.
---

# Monica Module Architecture

Choose boundaries from the capability being built. Apply `$monica-coding-standards` to responsibility ownership, abstraction choices, and supported behavior. Start with a few root layers; introduce feature folders when independent subdomains emerge. Keep `Modules/Module{Name}.cs` focused on module strategy, options, registration, dependencies, and DI. Keep project-level localization markers and JSON in `Localization/`, including in feature-oriented projects.

The host-facing path is `Minimal API or UI → Facade → internal Service → Provider`. Facades live in the infrastructure module, expose `Res`/`Res<T>` where the public boundary uses result envelopes, and stay thin. Other modules use public `Abstractions/` and `Models/` rather than another module's Facade. Internal services use ordinary .NET returns and exceptions; providers implement replaceable infrastructure strategies rather than business orchestration. Public developer-facing attributes belong in `Annotations/`. See [module-layout.md](references/module-layout.md) for folder and visibility decisions, and `$monica-development` for runtime registration and result semantics.

Every module derives from `MonicaModule<TOptions>`. `IUIModule` identifies UI composition, `IWebModule` identifies middleware or endpoint contributions, and `IWebHostRequiredModule` identifies an intrinsic web-host requirement. These are independent decisions. For an opt-in web feature, the selecting registration extension calls `RequireWebHost(reason)`. Use the named `ModuleWebStage.BeforeRouting` or `AfterRouting` boundary for middleware; integer priorities are not part of the contract.

UI pages compose components and delegate evidence state and business orchestration. The consuming component owns its route lifetime and drains admitted work; an injected circuit-scoped state is borrowed. UI code injects infrastructure Facades directly, even when UI and infrastructure share an assembly. Use [ui-layout.md](references/ui-layout.md) for standalone, composite, or mixed placement and extraction decisions. For component implementation and styling, use `$monica-ui-development`; for resource localization, use `$monica-ui-localization`.

For real restructuring examples, read [refactoring-examples.md](references/refactoring-examples.md). For page state and async coordination, read [page-state-pattern.md](references/page-state-pattern.md). Use `$monica-opentelemetry` when designing metric ownership or instruments.
