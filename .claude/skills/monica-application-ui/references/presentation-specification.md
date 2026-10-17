# Application Presentation Specification

Application UI is the presentation boundary of the existing DDD application. It follows its use-case vocabulary and ownership; it does not duplicate backend entities, repositories, or DomainServices in a frontend domain layer. A deployment decision and a project-reference decision are independent: one process can host a business UI and an operational cockpit with distinct navigation.

## Default solution layout

The default profile is Interactive Server Blazor. Extend the selected backend architecture profile with one presentation library per product, not one library per domain:

```text
src/
├── AppHost/Product.Web/
│   ├── Product.Web.csproj
│   ├── Program.cs
│   └── App.razor                 # Optional host-owned document root
├── Presentation/Product.UI/
│   ├── Product.UI.csproj         # Razor class library
│   ├── Layout/                  # Product layout and navigation
│   ├── Features/
│   │   └── Work/
│   │       ├── Pages/
│   │       ├── Components/
│   │       ├── State/
│   │       └── Models/          # Form and display models
│   ├── Components/              # Shared within this product
│   ├── Localization/
│   └── wwwroot/
├── Domains/                     # Or the adopted microservice topology
└── Shared/
    ├── Platform.Infrastructure/
    ├── Platform.Protocol/
    │   └── PublishedLanguages/DomainDelivery/{Requests,Models}/
    └── Platform.BuildingBlocks/
```

Create only useful folders. Host names are illustrative; an existing executable need not be renamed. A thin document/router is host composition. Product layout, navigation, pages and state belong in the presentation library. Modules are optional infrastructure composition mechanisms, never prerequisites for business pages. A host with no `.razor` files must set `RequiresAspNetWebAssets` to `true` to include the Blazor startup script; see [host composition](../../monica-infra-ui/references/application-hosting.md).

| Owner | Allowed application references |
| --- | --- |
| AppHost | Its adopted backend composition and product presentation |
| Presentation | Platform.Protocol and selected Monica UI/component packages |
| Backend projects | The existing architecture profile's dependency chain |

The explicit new edges are `AppHost -> Presentation` and `Presentation -> Platform.Protocol`. Presentation must not reference `Domains.*`, backend service implementations, or `Platform.Infrastructure`. Backend projects must not reference presentation. Public CLR visibility does not authorize consuming entities, DbContexts, repositories or handler implementations from UI.

Backend shared-project closures can include server libraries. This first profile therefore does not claim WebAssembly compatibility. A browser client requires a separate compatible contract closure or generated DTO/client boundary. A separate server UI uses published HTTP APIs; it does not load remote backend implementations. Do not add a BFF, client-abstraction project or independent deployment without an actual consumer need.

## Ownership

Organize UI features by user tasks, even when a task displays several bounded contexts. UI owns selection, filters, form drafts, loading state, display mapping, layout and navigation. Protocol owns stable command/query and result contracts. Keep UI-only validation messages, localized display text and component references out of Protocol.

A business decision or atomic cross-context operation remains a backend use case. Several UI commands commit separately; sequencing them in a component does not create one transaction. Domain invariants remain on their natural domain owners.

Reuse Monica's foundation for themes, icons, browser preferences and overlays. `ModuleUIHost` owns hosting and route discovery; `ModuleShellUI` is optional operational navigation. Product pages can use their own layout while registered diagnostic destinations use the Shell. Native `@layout` takes precedence. Providers belong once above the router, not in each layout.

The repository application specification records the actual presentation projects, feature ownership, route/layout choices, caller and tenant model, invocation profile, backend dependencies and migration gaps. See the [reference application](../../../examples/Monica.ReferenceApplication/README.md) for a working composition without business UI modules.
