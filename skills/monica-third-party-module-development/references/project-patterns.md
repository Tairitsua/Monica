# Third-Party Project Patterns

## Choose repository, package, and runtime boundaries first

Use one package when its modules share ownership, versioning, support policy, release cadence, and consumers. Split packages only when users need to install or version capabilities independently.

One schema-v2 repository may release several aligned packages:

```text
<RepositoryId>/
├── monica.manifest.json
├── <RepositoryId>.slnx
├── src/
│   ├── <CapabilityPackage>/
│   ├── <ProviderPackage>/
│   └── <UiPackage>/
├── tests/
│   ├── Test.<CapabilityPackage>/
│   ├── Test.<ProviderPackage>/
│   └── Test.<UiPackage>/
└── containers/                 # optional provider services
```

Keep NuGet dependencies (`packageDependencies`) separate from manifest module declarations (`modules[].dependsOn`). Both use full distribution identifiers, while the scaffold compiles the latter into CLR-type runtime edges. Every cross-package module edge requires a package edge.

Local module names need be unique only inside their owning package. Package-owned namespaces keep
same-named modules in different packages distinct, and generated cross-package module and option
type references are fully qualified.

## Single infrastructure module

```text
<PackageId>/
├── Modules/Module{Name}.cs
├── Abstractions/
├── Models/
├── Facades/
├── Services/
└── Providers/
```

Derive the strategy from `MonicaModule<Module{Name}Option>`. Manifest ecosystem keys describe
distribution and repository edges; Monica uses the concrete strategy `Type` as runtime identity.

## Multiple modules in one package

```text
<PackageId>/
├── Modules/
│   ├── ModuleFeatureA.cs
│   ├── ModuleFeatureB.cs
│   └── ModuleFamily.cs          # optional aggregator
├── FeatureA/
│   ├── Abstractions/
│   ├── Models/
│   ├── Facades/
│   └── Services/
└── FeatureB/
    ├── Abstractions/
    ├── Models/
    ├── Facades/
    └── Services/
```

Each feature follows the normal Monica layer rules. Do not let one feature consume another feature's internal services; use public abstractions and models.

All independently published module registration units and `Add*` extensions use `<PackageId>.Modules`. References to Monica-owned module types continue to import `Monica.Modules`.

## Mixed infrastructure and UI package

Use this when UI is tightly coupled to the capability and does not need independent distribution.

```text
<PackageId>/
├── Modules/
│   ├── Module{Name}.cs
│   └── Module{Name}UI.cs
├── Models/
├── Facades/
├── Services/
├── Pages/
│   └── UI{Name}Page.razor
├── UI{Name}/
│   ├── Components/
│   ├── State/
│   └── Support/
├── Localization/
│   ├── {Name}Resource.cs
│   └── {Name}Resource/
│       ├── en-US.json
│       └── zh-CN.json
└── wwwroot/
```

Rules:

- The UI strategy derives from `MonicaModule<Module{Name}Option>` and implements `IUIModule`.
- A UI strategy adds `IWebModule` only if it contributes middleware or endpoints, and adds `IWebHostRequiredModule` only when those contributions are essential. UI identity itself does not imply web capability.
- The UI module declares concrete hard dependencies on the infrastructure module, `ModuleLocalization`, and `ModuleShellUI` in `Describe`.
- Register navigation with the UI module's own localization resource type. Derive its category ID from that UI module's key by removing only the final `.UI`; do not derive it from the package ID when a package contains multiple UI modules.
- Derive every public UI route from the package family after removing `<Publisher>.Monica.` and a distribution-only final `.UI` segment. Convert the remaining PascalCase segments to readable kebab-case words: `Tairitsua.Monica.GachaPool` owns `/gacha-pool` and subroutes such as `/gacha-pool-history`. The host route namespace is shared, and Monica rejects duplicate normalized routes, so use distinct package families or package-family subroutes for extensions that must coexist. Never claim an unrelated generic route such as `/dashboard`.
- Pages inject Facades directly. Do not add a UI service wrapper around a Facade.
- Keep page code thin and move state/orchestration to `UI{Name}/State` or `Support`.
- Use isolated `.razor.css`, MudBlazor primitives, and Monica/MudBlazor theme tokens.

Configure localization and shell contributions intrinsically from the UI module's `Describe` method:

```csharp
module.Require<ModuleLocalization, ModuleLocalizationOption>(
    static option => option.AddResource<AuditResource>());
module.Require<ModuleShellUI, ModuleShellUIOption>(
    static option => option.ConfigureNavigation(registry =>
{
    var category = registry.RegisterLocalizedCategory<AuditResource>(
        "Acme.Monica.Toolkit.Audit",
        "Navigation:Category",
        order: 450);
    registry.RegisterLocalizedPage<UIAuditPage, AuditResource>(
        "/toolkit-audit",
        "Navigation:Title",
        Icons.Material.Filled.Extension,
        categoryId: category,
        addToNav: true,
        navOrder: 80);
}));
```

Use deterministic explicit values. The scaffold assigns category order `450 + UI module index`, after the shell's Configuration category, and page navigation order `80 + UI module index`. Keep category identity stable when labels, cultures, or page titles change.

## Provider packages

Use `<Publisher>.Monica.<Capability>.<Provider>` when the provider has a genuinely independent dependency/release boundary. A provider module should implement public abstractions from the capability package and expose an extension such as `UseNatsProvider()` on the target capability's `ModuleRegistration`.

Represent the module as `kind: provider`, set `providerFor` to the capability module's full manifest key, and repeat that key in `dependsOn`. The provider project references the capability project; its packed NuGet package depends on the capability package and must not embed the capability assembly.

```csharp
public static ModuleRegistration<ModulePaddleOCR, ModulePaddleOCROption> UsePaddleOCRProvider(
    this ModuleRegistration<global::Acme.Monica.AI.OCR.Modules.ModuleOcr,
        global::Acme.Monica.AI.OCR.Modules.ModuleOcrOption> target,
    Action<ModulePaddleOCROption>? configure = null)
{
    return target.Include<ModulePaddleOCR, ModulePaddleOCROption>(configure);
}

public sealed class ModulePaddleOCR
    : MonicaModule<ModulePaddleOCROption>, IModuleProvider
{
    public Type ProvidesFor => typeof(global::Acme.Monica.AI.OCR.Modules.ModuleOcr);

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<global::Acme.Monica.AI.OCR.Modules.ModuleOcr,
            global::Acme.Monica.AI.OCR.Modules.ModuleOcrOption>();
    }
}
```

`Include` selects the optional provider without reversing ownership. The provider's `Describe` method supplies the runtime hard edge back to the capability. The manifest `providerFor` and `dependsOn` keys let packaging tools resolve and verify those concrete types; they are not runtime module identities.

## Provider connector plus OCI service

Use this boundary when the native/runtime dependency is too large, platform-specific, or operationally better isolated from the .NET host:

```text
<CapabilityPackage>           provider-neutral .NET contract
<CapabilityPackage>.<Provider> HTTP/gRPC connector and provider module
<CapabilityPackage>.UI       optional interactive test UI
containers/<provider>/       real service and shared CPU/GPU Dockerfile DAG
docker-bake.hcl              immutable CPU/NVIDIA build targets
```

Put CPU and NVIDIA variants in one OCI repository with different immutable tag suffixes. `companionPackageId` points to the package that owns the provider module. Share dependency, application, and model stages before the runtime targets diverge. Make the connector API independent of accelerator choice so hosts switch image tags without recompiling.

The image must run as non-root, expose a health check, carry OCI/Monica labels, and fail fast when a declared GPU mode is unavailable unless fallback is explicit product behavior. Validate GPU support with a real provider inference, not only image construction or `nvidia-smi`.

Do not generate optimistic release automation. Add `releaseGates` only when repository-owned smoke scripts can start each image, exercise meaningful provider output through the public protocol, prove CPU/NVIDIA execution, clean up, and fail the job on any mismatch. NVIDIA gates declare a managed self-hosted runner label set. If any image lacks complete gates, the scaffold omits the publish workflow for the whole aligned release; with complete gates, image load/inspection and all smoke commands run before authentication or push.

## Tests

- Pure value behavior: direct construction.
- Module registration/options/dependencies: `MonicaTestApplicationFactory<TDiscoveryAnchor>`.
- Give every independently published package its own `tests/Test.<PackageId>` project; this intentionally overrides the first-party `Test.Monica.*` convention.
- Register graph entry modules only in the composition factory so dependency options are proven transitively. For UI modules, resolve the host-owned read-only `IPageCatalog` and assert the contributed page, category, navigation metadata, and module-owned localization resource marker. The write-only `INavigationRegistryBuilder` exists only inside startup contribution callbacks.
- Facade flows: public `Result`/`Result<T>` assertions plus observable boundary state.
- UI component logic: bUnit.
- Full visual behavior: bridge host plus Playwright at desktop and narrow widths.

For a runnable .NET 10 bridge, use `Microsoft.NET.Sdk.Web` and set
`<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>` in the bridge
project. This ensures the host emits the framework asset endpoints required by
`/_framework/blazor.web.js`; without it, static prerendering may appear while
the dashboard never becomes interactive.
