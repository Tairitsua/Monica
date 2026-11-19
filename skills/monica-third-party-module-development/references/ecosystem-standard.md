# Monica Ecosystem Standard v1

## 1. Scope and governance

This standard covers independently owned NuGet packages that integrate with Monica. Conformance is self-attested in v1. Monica does not review, certify, support, or warrant third-party packages merely because they follow this standard or use the compatibility mark.

## 2. Package identity

Use:

```text
<Publisher>.Monica.<Package>[.<Variant>]
```

Rules:

- `Publisher` is the NuGet owner, organization, or durable product identity.
- Every segment matches `[A-Za-z][A-Za-z0-9]*`.
- Separate segments with dots only. Do not use hyphens, spaces, or underscores.
- Keep the complete ID to 100 characters or fewer.
- `Publisher` must not be `Monica` under case-insensitive comparison.
- Match the project file name, `PackageId`, `AssemblyName`, and `RootNamespace` exactly.
- Use variants only for real distribution boundaries such as a separately shipped provider or UI companion.
- Do not add `.Community`, `.Unofficial`, `.OpenSource`, license names, or maturity labels to the ID.

Examples:

```text
Contoso.Monica.FeatureFlags
Contoso.Monica.EventBus.Nats
Contoso.Monica.EventBus.Nats.UI
```

## 3. Multiple modules in one package

A package may expose any coherent number of Monica modules. Mirror Monica's official package organization:

- place one registration unit per `Modules/Module{Name}.cs`
- use feature folders when the package contains independent capabilities
- derive every strategy from `MonicaModule<Module{Name}Option>` and declare dependencies in `Describe(ModuleDescriptor)` with `module.Require<ModuleX>()`. Use the typed overload when contributing dependency defaults.
- provide an aggregator module only when registering the full bundle is a meaningful user action
- do not create a package per module merely to satisfy this standard

The package ID is the ownership prefix for all manifest ecosystem keys. A key must either equal the package ID or start with `<PackageId>.`.

```text
Package: Acme.Monica.Observability
Keys:
  Acme.Monica.Observability.Logging
  Acme.Monica.Observability.Tracing
  Acme.Monica.Observability.UI
```

Manifest keys are compared with ordinal case-insensitive semantics and preserve canonical casing in the repository contract and diagnostics. They identify distribution metadata and cross-package declarations only; Monica's runtime graph, option access, provider target, and diagnostics use concrete CLR module `Type` identity. A third-party UI manifest entry uses `.UI` as its final key segment, while its module class explicitly implements `IUIModule`.

Third-party UI routes use the package family without the publisher-owned identity prefix. Remove the leading `<Publisher>.Monica.` segments and a distribution-only final `.UI` segment, split the remaining PascalCase segments into lowercase kebab-case words, and join the result with hyphens. For example, `Tairitsua.Monica.GachaPool` uses `/gacha-pool`, while `Acme.Monica.Analytics.UI` uses `/analytics`. A UI capability not already represented by a package-family segment appends its own name, so an Audit UI module in `Acme.Monica.Toolkit` uses `/toolkit-audit`.

The resulting route namespace is shared by the host rather than isolated by publisher. Keep every contributed route at the package-family prefix or a descriptive extension such as `/gacha-pool-history`. Monica rejects duplicate normalized routes during registration; packages that must coexist therefore need distinct package families or distinct package-family subroutes. Do not claim an unrelated generic route such as `/dashboard` or `/settings`.

Navigation category identity follows a different rule from routes: derive it from each UI module's full manifest ecosystem key by removing only the final `.UI` segment. For example, `Acme.Monica.Toolkit.Audit.UI` owns category ID `Acme.Monica.Toolkit.Audit`. This keeps categories collision-resistant across publishers and gives multiple UI modules in one package independent identities.

Every UI module contributes its category and localized pages through one `ModuleShellUIOption.ConfigureNavigation` callback declared by its concrete Shell dependency in `Describe`. It contributes the same module-owned resource marker through its concrete Localization dependency. Register `Navigation:Category` through `RegisterLocalizedCategory<TResource>` with an explicit deterministic order, assign the returned ID, and pass it to `RegisterLocalizedPage<TPage, TResource>` through `categoryId`. The scaffold's primary page uses `Navigation:Title`; additional pages use distinct module-owned `Navigation:*` keys. Every navigation page sets `addToNav: true` and an explicit navigation order. `RegisterLocalizedComponent` is a legacy API and is not ecosystem-v1 compliant.

## 3.1 Multiple packages in one repository

Repository manifest schema v2 may describe several independently consumable packages without changing this v1 package-identity standard.

- Keep one packable project per declared package.
- Use full package IDs for repository-internal NuGet dependencies.
- Use full manifest ecosystem keys for repository dependency declarations, including dependencies within the same package; generated runtime edges use concrete CLR types.
- Back each cross-package module dependency with a NuGet package dependency.
- Implement provider modules with `IModuleProvider`, declare their `providerFor` manifest target, return `typeof(TargetModule)` from `ProvidesFor`, and include that target through a concrete hard edge in `Describe`.
- Keep package and runtime graphs acyclic.
- Do not embed a sibling package assembly in place of a package dependency.

One aligned repository release may also publish a provider-service OCI repository. Its `companionPackageId` names a package that owns a provider module. CPU and GPU variants use separate immutable tags under the same OCI repository and expose the same connector-facing API. OCI distribution does not alter NuGet ownership, CLR runtime identity, or v1 compatibility status. Automated publication remains disabled until provider-specific CPU and applicable NVIDIA inference gates are declared.

## 4. Public API naming

For each module named `{Name}`:

| Surface | Pattern |
|---|---|
| Module | `Module{Name}` |
| Options | `Module{Name}Option` |
| Strategy | `Module{Name} : MonicaModule<Module{Name}Option>` |
| Registration | `ModuleRegistration<Module{Name}, Module{Name}Option>` |
| Host entry point | `monica.Add{Name}()` inside `builder.AddMonica(...)` |
| Module file | `Modules/Module{Name}.cs` |

Put independently published registration types in `<PackageId>.Modules`, for example `Acme.Monica.Observability.Modules`. Consumers opt into the package with its publisher-owned namespace. Fully qualify cross-package module and option types; Monica-owned module types such as `ModuleLocalization` and `ModuleShellUI` remain in `Monica.Modules`. Keep business APIs in the package root namespace or its feature namespaces.

Implement `IUIModule` on UI strategies. Implement `IWebModule` on web-capable strategies and also implement `IWebHostRequiredModule` when their generated endpoints or middleware are essential; the standard scaffold treats `kind: web` as host-required. These markers describe runtime capabilities independently of manifest keys.

## 5. Package metadata

Require:

- `PackageId`, `Version`, `Authors`, `Description`, `PackageTags`
- `PackageProjectUrl`, plus `RepositoryUrl` and `RepositoryType=git` only when source is consumer-accessible
- `PackageReadmeFile` and an embedded README
- `PackageIcon` and an embedded 64×64 or larger transparent PNG
- exactly one license expression or packed license file
- XML documentation and symbols, plus Source Link and repository metadata where source is consumer-accessible
- tags `monica`, `monica-module`, and `monica-ecosystem-v1`

Add `monica-ui` for UI packages and at least one useful package-specific capability/provider tag. Never copy Monica's first-party author, company, repository, or copyright metadata.

## 6. Compatibility and quality

- State the minimum supported Monica version.
- A stable package must not depend on a prerelease Monica package. Keep the extension prerelease until its Monica dependency is stable.
- Avoid exact dependency pins and upper bounds unless a verified incompatibility requires them.
- Treat warnings as failures in CI.
- Test all public registration paths, including transitive dependencies and multiple hosts when state ownership matters.
- Pack and restore from a local feed before publishing.
- Never ship secrets, machine-specific paths, build outputs, or source-project references.

## 7. Branding

`Monica.*` and the official purple `#512BD4` mark identify first-party packages. Third parties may use their own icon or the canonical emerald `#10B981` Monica Compatibility Mark. The compatibility asset deliberately preserves the Monica silhouette in a fixed emerald colorway so the ecosystem relationship is recognizable while the purple official identity remains unambiguous. Use the supplied asset unchanged; do not recolor the official file, invent another colorway, or alter the compatibility artwork.

The package validator verifies the compatibility PNG against the canonical asset contents; renaming modified artwork to `monica-compatibility-mark.png` does not satisfy the branding contract.

The mark means only that the publisher self-attests compatibility with this standard. It does not mean official, certified, verified, endorsed, or supported by Monica. Include the standard self-attestation and independence notice in the package README.
