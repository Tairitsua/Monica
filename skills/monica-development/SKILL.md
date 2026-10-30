---
name: monica-development
description: Implement or refactor Monica framework modules, registration, discovery, diagnostics, Facades, result envelopes, and hosted services. Use for module internals; application consumers use the matching monica-infra skill.
---

# Monica Framework Development

Apply `$monica-coding-standards` during implementation and review; use `$monica-architecture` for layer and public-boundary decisions. This skill owns the framework module lifecycle and deliberate `Res` boundaries. A module strategy derives from `MonicaModule<TOptions>`; its public `IMonicaBuilder` entry returns a host-bound `ModuleRegistration<TModule,TOptions>` inside `builder.AddMonica(...)`. The concrete module `Type` is graph identity. `ModuleKey` is diagnostic only. First-party strategies and registration extensions live in `Monica.Modules`; independently published packages use `$monica-third-party-module-development`.

Declare hard dependencies, optional ordering, and baseline feature requirements in `Describe(ModuleDescriptor)`. Keep graph shape independent of options. A provider or optional capability can call `SatisfyFeature(...)`; a selected capability that requires the web host calls `RequireWebHost(reason)`. Keep baseline behavior in the module so direct and transitive registration behave alike. Options hold developer configuration, not mutable runtime registries. See [registration-runtime.md](references/registration-runtime.md) for lifecycle, state, discovery, and diagnostics contracts.

`IUIModule`, `IWebModule`, and `IWebHostRequiredModule` express separate facts. Implement `IWebModule` for actual endpoint or middleware contributions, and use named `ModuleWebStage` boundaries. A UI-only composition module can be `IUIModule` without a web marker. An opt-in web feature should add its requirement through registration rather than make the entire module web-host-only.

Facades at a deliberate host/UI boundary use `Res` or `Res<T>`; internal services return normal .NET types and throw exceptions. Preserve `Res<string>` data with `Res.Ok<string>(value)`: `Res.Ok(value)` binds to the non-generic message overload. `IsOk` says 200 or 201, not that `Data` exists. For safe error projection, remote-call classification, and result handling, read [res-type-guide.md](references/res-type-guide.md). For background work using `MoBackgroundService` or `CoordinatedLeaderService`, read [hosted-service-guide.md](references/hosted-service-guide.md).

When module code consumes an existing capability, consult the relevant `$monica-infra-persistence`, `$monica-infra-messaging`, `$monica-infra-jobs`, `$monica-infra-configuration`, `$monica-infra-hosting`, `$monica-infra-observability`, `$monica-infra-ai`, `$monica-infra-web`, or `$monica-infra-ui` skill for its public setup contract. This skill remains the authority for authoring framework modules. Read the [C# contracts](../monica-coding-standards/references/csharp-contracts.md) when writing developer-facing APIs, options, or registration extensions.
