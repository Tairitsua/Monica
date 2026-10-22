# Shared domain metadata

`ModuleDomainCatalog` lives in `Monica.Core`. It owns the host's shared `DomainInfo` metadata and requires no application model, service registry, transport, or UI. Configure labels once and inject `IDomainCatalog` into any consumer instead of building another enum map or implementing a discovery-specific domain provider.

```csharp
using Monica.Core.Domains.Abstractions;
using Monica.Core.Domains.Models;
using Monica.Modules;

builder.AddMonica(monica =>
{
    monica.AddDomainCatalog(options => options.AddEnum<ProductDomain>());
});

// In a UI, diagnostics service, or error-message provider:
var displayName = catalog.GetDisplayName("Ordering"); // catalog is an injected IDomainCatalog.
```

An existing domain enum remains the identity source. `AddEnum<TEnum>()` imports member names as IDs and `DescriptionAttribute` values as display names, falling back to the member name. For flags enums it excludes zero and composite members; ordinary enums retain zero. Aliases for imported values fail configuration. For string identities, add `new DomainInfo { Id = "Ordering", DisplayName = "Orders", Description = "Order management" }` to `options.Domains` instead. Do not configure the same ID through both mechanisms.

`DomainInfo` is an immutable record with a required `Id`, optional `DisplayName`, and optional `Description`. IDs are ordinal, nonempty, and have no surrounding whitespace. Explicit duplicate metadata fails composition. The catalog defaults to empty and publishes a frozen dictionary through `IDomainCatalog.Domains`; it is a singleton within one host, not process-global state. Configuration is startup-only and requires a new host to change. `GetDisplayName(id)` falls back to the raw ID for a missing or blank label and does not add unknown IDs to the catalog.

`AddApplicationModel(...)` and `AddServiceDiscovery(...)` require this module automatically. ApplicationModel contributes its complete compiled domain IDs through `IDomainCatalogSource`, including domains with no local implementation. Explicit metadata takes precedence over these identity-only contributions. Other framework integrations may implement this source contract to contribute stable startup IDs; ordinary applications should configure `AddDomainCatalog` directly. A source must not depend on `IDomainCatalog` or derive identities from a changing instance registry.

The shared record describes a business domain. Assembly ownership stays in ApplicationModel, roles and placement stay in its deployment model, and online instances stay in ServiceDiscovery. They reference the same domain ID rather than duplicating display configuration or adding deployment fields to `DomainInfo`. Observing an unknown domain in the registry does not silently register it in the catalog.

When migrating, replace `Monica.ServiceDiscovery.Models.DomainInfo.Name` with `Monica.Core.Domains.Models.DomainInfo.Id` and configure `AddDomainCatalog`. `IServiceDiscoveryCatalogProvider` now supplies only predefined applications; it no longer owns domain metadata. See [application-model.md](application-model.md) for compiled ownership and [service-discovery.md](service-discovery.md) for topology and registration evidence.
