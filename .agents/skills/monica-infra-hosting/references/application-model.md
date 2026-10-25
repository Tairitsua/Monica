# Unified application model

`Monica.ApplicationModel` composes compiled business ownership, each artifact's actual local implementations, and startup placement. It depends on Core and does not require HTTP, persistence, or API. Use the shared [application specification](../../monica-application/SKILL.md) for the solution layout; this reference owns registration and runtime behavior.

## Declare ownership once

The existing product domain enum remains authoritative. Configure its display metadata once through the Core [domain catalog](domain-catalog.md); ApplicationModel contributes the compiled IDs without duplicating labels. A domain core declares its domain, and each role library declares one exact role and its owning domain:

```csharp
// Domains.Ordering
[assembly: ApplicationDomain(ProductDomain.Ordering)]

// Roles.Ordering
[assembly: ApplicationRole("ordering", ProductDomain.Ordering)]

// Roles.Ordering.Reporting
[assembly: ApplicationRole("ordering.reporting", ProductDomain.Ordering)]

// Each business host: references its included cores/roles, Protocol, the shared catalog when used, and generator analyzer.
[assembly: ApplicationHost]
```

These attributes are in `Monica.ApplicationModel.Annotations`. Domain values normalize to the named enum member or exact string. Identifiers are ordinal; dots do not imply role inheritance. No public marker type or repeated handler-assembly list is required. The protocol project owns published API interfaces; each host emits an `IApplicationManifest`, caller implementations and its local receivers. Actual local annotated implementation assemblies must remain in Monica's discovery inventory. Remote ownership metadata does not require loading those implementations. Startup rejects duplicate ownership, invalid local handlers, incomplete placement and selection of a role the current artifact does not contain.

Domain and role IDs must also remain distinct under case-insensitive configuration lookup. Exact role matching still applies: `ordering` does not select `ordering.reporting`, and `Ordering` does not match `ordering`.

The host generator validates known published operations against the shared catalog and local implementation inventory. A missing implementation for a locally included role, or multiple local handlers including a conflicting plain `IRequestHandler`, fails generation. A plain internal mediator request does not become a generated application API. These compile-time checks keep the manifest's operation owner aligned with the actual executor.

`WorkloadOwner` is an optional named argument on `ApplicationRole`. It defaults to the role ID and remains independent of AppId. Use an explicit value only to preserve an existing durable scheduler namespace, such as an old executable project name. Different roles cannot share that namespace: their snapshots would retire each other's jobs. Moving a role between deployments does not rename its job keys or owner.

## Generate a lightweight shared catalog

Use a catalog when hosts reference different implementation sets. Keep ownership declarations in domain and role assemblies; do not duplicate their operation lists or role/domain registry in deployment files.

- Create a lightweight catalog project with `<MonicaApplicationCatalog>true</MonicaApplicationCatalog>` and a normal reference to Protocol.
- Add the Monica API generator analyzer to annotated core and role projects so their compiled assemblies export owned metadata.
- Reference those implementations from the catalog only as metadata build inputs: `ReferenceOutputAssembly="false"`, `Private="false"`, and `PrivateAssets="all"`.
- For source consumption, import `Monica.Generators.AutoController/build/Monica.ApplicationCatalog.targets` in the catalog project. The NuGet package includes the same integration as `build/Monica.Generators.AutoController.targets`, which NuGet imports automatically.
- Reference the catalog normally from each Host, retain `[ApplicationHost]`, and reference only the domain/role implementations that artifact actually executes.

The build target reads metadata exports from referenced outputs and supplies deterministic compiler `AdditionalFiles`. Missing exports on annotated inputs fail the build. The generator does not perform source-generation filesystem I/O or execute implementation assemblies. The resulting catalog contains domain/role/published-operation descriptions without runtime references to their concrete types. Private HTTP endpoints and nonpublished requests are discovered only from local implementations; they are not exported as remote operations.

Design-time builds supply the same metadata inputs to the IDE from existing producer outputs. They query output paths without building producers. Build the solution once after a clean checkout, and rebuild after ownership or published contract changes. Excluding catalog collection from `DesignTimeBuild` leaves the IDE without remote ownership and can cause `AC1026` and missing generated manifests even when the normal build succeeds; suppressing those diagnostics does not repair the missing inputs.

`IApplicationManifest.LocalAssemblies` identifies the annotated implementation assemblies actually included in that executable. Its logical `Domains`, `Roles` and published `Operations` may also describe remote implementations from the catalog. Every listed local assembly must be present in discovery; omitting an implementation from discovery is not a way to declare it remote. Each host adds its local private operations to this manifest.

Build-only input projects may still be restored and built. They must not become compiler or runtime references of the catalog or copy native assets into a host that omits them. Check the normal host's publish output and `.deps.json` when separating OCR/GPU dependencies. Do not solve missing metadata by restoring a runtime reference to the heavy role. Host-wide middleware and product provider defaults remain in the host; included implementations declare their own registrations through the assembly attributes below.

## Compiled API dependency evidence

Annotated domain and role compilations export statically resolved calls to published `IApplicationApi` methods. Each `ApplicationDependencyDefinition` retains the calling assembly, calling type, and published request type; the model resolves those to source domain/role and target operation ownership. An unused injected API, a project reference, or a generated client alone does not create a dependency. The product catalog carries this evidence as metadata, so selective hosts can inspect callers whose implementation assemblies they do not load. Invalid source ownership or missing target operations fail catalog validation. Rebuild producer assemblies, the catalog, and consuming hosts after changing calls.

`IApplicationManifest.Dependencies` and `IApplicationModel.Dependencies` describe collected static API usages. They are independent of selected roles, Local/HTTP routing, AppIds, instance counts, and observed traffic. Source roles may execute passive calls locally while not active as autonomous workloads. Do not convert these records into presumed HTTP edges between owning applications. Manual manifests must supply their own evidence; an empty list means no collected evidence.

Coverage is deliberately limited to direct calls resolved by the compiler in annotated domain/role source. Reflection, dynamic dispatch, method-group indirection, unowned shared helpers, EventBus relationships, and external integrations are not a complete runtime call graph and are not inferred. The service-discovery cross-domain view groups these records by domain pair, omits same-domain edges, and retains calling types and operations for inspection.

## Compose placement

Supply one complete application map in startup configuration:

```json
{
  "Application": {
    "AppId": "orders-api"
  },
  "Deployment": {
    "Applications": {
      "orders-api": {
        "AppName": "Order API",
        "Roles": [
          "ordering"
        ]
      },
      "orders-reports": {
        "AppName": "Order reports",
        "Roles": [
          "ordering.reporting"
        ]
      },
      "orders-tools": {
        "Roles": []
      }
    }
  }
}
```

```csharp
builder.AddMonica(monica =>
{
    monica.AddApplicationModel();
    monica.AddApplication();
    monica.AddApplicationInvocation();
    // Configure the API HTTP provider for omitted implementations or remote role/authority destinations.
});
```

Import `Monica.Modules`. `Deployment:Applications` selects the highest-priority provider that supplies this structural value and reads that provider alone. Entries and indexed role arrays never merge with lower providers. Explicit null, an empty map, or an incomplete higher-priority map does not request fallback. An entry must explicitly supply `Roles`; `[]` is valid. Unknown fields, roles, missing owners, duplicate assignments, and case-ambiguous AppIds or role IDs fail composition. AppName defaults to the map key when omitted. The selected entry supplies Core identity and service discovery; a contradictory direct AppName fails.

For code-defined topology, assign `ModuleApplicationModelOption.Applications` using `ApplicationPlacement` from `Monica.ApplicationModel.Models`. Its null default selects configuration; even an explicitly empty code map conflicts with a configured map. Use one authority. `ApplicationDeployment.Create` remains available for standalone validation, but host composition prepares and publishes its own single immutable snapshot. `IApplicationModel.Deployment` and the DI-registered `ApplicationDeployment` are that same instance; `Applications` includes all entries with copied, read-only role lists, while `RoleApplications` is the derived reverse index.

Every known product role has exactly one logical owning AppId in the deployment scope; every role assigned to the current AppId must have a local implementation in this artifact. The current AppId must have an entry, which may select no roles. Replicas share the same AppId and selected roles. A combined deployment places several included roles in one entry; there is no synthetic `all` role. A normal artifact can run under several AppIds, and a justified specialized host can omit other implementations. Project references select available code; deployment values select active roles and workload images. Configuration cannot activate an omitted implementation. Changing the map or its input collections after preparation cannot change the running model; restart to select a new topology.

`ManifestType` on `ModuleApplicationModelOption` overrides generated-provider discovery for an explicitly supplied compiled manifest. Its concrete type must implement `IApplicationManifest` and have a parameterless constructor. It is instantiated once during composition, not resolved from a temporary service provider. It does not infer missing ownership, install implementation code or bypass catalog/local-inventory validation. Prefer the generated catalog and host manifest for selective artifacts.

## Register included implementations

Name an optional registration type on the owning assembly attribute:

```csharp
[assembly: ApplicationRole("ordering.reporting", ProductDomain.Ordering,
    Registration = typeof(ReportingRegistration))]

public sealed class ReportingRegistration : ApplicationRegistration
{
    public override void ConfigurePassive(ApplicationRegistrationContext context)
    {
        context.Services.AddScoped<ReportReader>();
    }

    public override void ConfigureActive(ApplicationRegistrationContext context)
    {
        context.Monica.AddJobScheduler().UseInMemoryStore();
    }
}
```

`ApplicationRegistration` and `ApplicationRegistrationContext` are in `Monica.ApplicationModel.Abstractions`. The type must belong to the declaring assembly, be concrete and closed, derive from `ApplicationRegistration`, and expose a public parameterless constructor. A role without explicit registration needs no placeholder class. Remote catalog entries contain ownership metadata and never execute registration code.

For a domain core, use `ApplicationDomain(..., BackgroundRole = "ordering.reporting", Registration = typeof(OrderingRegistration))` when it has an active registration or core-owned autonomous resources. BackgroundRole must name a declared role in the same domain. Overriding `ConfigureActive` or discovering an autonomous core resource without it fails because that work has no execution owner. Passive services and ProcessLocal initialization need no BackgroundRole. An explicit declaration can anchor a genuinely shared implementation to its domain/role; it cannot silently reassign a concrete role implementation to a different role or domain. Outbox ownership is evaluated per context, not from the shared framework worker.

`AddApplicationModel` records intent. After the host's AddMonica callback, Core discovers the initial dependency graph and finalizes ApplicationModel options once, retaining normal dependency-default-before-host precedence. ApplicationModel validates and freezes its inventory and topology and projects identity. Core then freezes identity and discovery. All included registrations run `ConfigurePassive`, followed by selected registrations' `ConfigureActive`, sorted deterministically by owner ID. Each registration is constructed once and receives both callbacks on that same instance when active. Newly declared modules enter the normal lifecycle afterward.

The context exposes `Monica`, `Services`, frozen `Configuration`, `Environment`, `IsActive`, and `IsAutonomous`. IsActive means local placement; ConfigureActive still declares eligible integrations in Manual mode. IsAutonomous also requires the frozen Application:AutonomousWork permission. The `GetStartupOptions<T>()` extension from `Monica.Configuration.Bootstrap` also accepts this context without making ApplicationModel depend on Configuration. Callbacks register descriptors and modules; they must not resolve services, open connections, start tasks, or rely on another owner's callback order. They cannot change identity, topology, discovery, configuration-source selection, or recursively run another owner's registration. Deferred module contributions and service descriptors retain their declaring registration owner.

## Activation boundaries

`IApplicationModel` exposes domains, roles, operations, placement, and exact role lookup. `LocalHandlers` is an `IReadOnlyDictionary<Type, Type>` from request type to verified local handler type; remote-only operations have no entry. `IsRoleAvailable(roleId)` reports whether the artifact includes that exact role's implementation, while `IsRoleActive(roleId)` reports whether the current AppId selects it. A local binding alone does not bypass a request's required active role. Core receives the same immutable instance as `ITypeActivationPolicy` before registration commits. Module callbacks and finalizers cannot remove, replace, or duplicate its runtime singleton registration; composition rejects these changes before any replacement factory executes.

| Entry | Behavior for implementations included in this artifact |
| --- | --- |
| Ordinary application handlers, repositories, passive services | Available when included, regardless of the code-owning role's placement |
| In-operation local domain events and local EventBus handlers | Remain available; role selection does not suppress business rules |
| Distributed EventBus subscriptions | Selected role only; Manual holds external participation while a dual local/distributed handler retains local behavior |
| Job definitions and execution | Selected role only, grouped by stable workload owner; Manual uses a fresh local memory store and native explicit executor without automatic scheduling or shared queue participation |
| Concrete and factory `IHostedService` registrations | Manual and Disabled descriptors are held without resolving factories; declared ownership survives deferred callbacks |
| Startup seeders | Manual retains definitions for explicit dependency-closed runs; ProcessLocal initialization may run independently of placement but cannot auto-start held prerequisites |
| DataChannel setup, hubs, actor runtime integrations | Declare eligible integrations in ConfigureActive; each native capability must gate unsolicited work rather than treating callback invocation as automatic permission |
| Generated HTTP receivers | Selected per operation, including mixed-role interfaces/controllers |

Multiple selected DataChannel setup contributors run before materialization. Each setup type is registered once, and channel IDs must remain unique. A selected role with no remaining jobs publishes an empty owner snapshot to retire removed definitions; an inactive role never retires another deployment's jobs.

When an operation truly requires an active integration or process-owned state, declare `[RunOnRole("ordering.reporting")]` on its request. That role must be the operation's code owner. Generated invocation uses Local only when the implementation is present and the role is active, and otherwise resolves its remote owner. Direct mediator execution is guarded before constructing the handler. Ordinary calls prefer Local when their implementation is included; omitted published operations use their known catalog owner and placement through HTTP. Receivers require an included implementation and an active role. A receiver never forwards recursively. A role is an execution/composition boundary, not an authorization or database isolation boundary.

See [API](../../monica-infra-messaging/references/invocation.md) for transport and authority policy, [transactions](../../monica-infra-persistence/references/transactions.md) for domain-owned persistence, and [service discovery](service-discovery.md) for observed registration metadata.
