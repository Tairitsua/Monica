# Unified application architecture

Use one development model for combined and split deployments. Business ownership belongs to domains, application responsibilities belong to domain-owned roles, and project references determine which implementations a host artifact contains. Deployment configuration places roles under logical AppIds. A normal host may compose most roles while a separate host carries OCR, GPU, native, licensed, or otherwise incompatible runtime dependencies. Create another host for a real dependency or runtime composition need, not for every role or AppId.

## Physical ownership

```text
src/
  Shared/
    Platform.Protocol/
      Application/                         # Stable role IDs and existing domain identifiers
      PublishedLanguages/DomainOrdering/{Requests,Models,Events}/
    Platform.ApplicationCatalog/           # Generated lightweight catalog when hosts select implementations
    Platform.BuildingBlocks/                # Shared technical mechanisms
    Platform.Hosting/                      # Product bootstrap and provider defaults
  Domains/
    Ordering/
      Domains.Ordering/
        {Entities,ValueObjects,DomainServices,Interfaces,Repository,Configurations}/
      Roles.Ordering/
        {HandlersCommand,HandlersQuery,HandlersEvent,Registration}/
      Roles.Ordering.Reporting/
        {HandlersQuery,BackgroundWorkers,Registration}/
    File/
      Domains.File/
      Roles.File/
      Roles.File.Ocr/                       # Native implementation remains owned by File
  AppHost/
    Product.Host/                          # Default business runtime composition
    Product.Ocr.Host/                      # Only when separate native dependencies are needed
  Presentation/
    Product.UI/                            # Optional product presentation
  Migrations/
    Migrations.DomainOrdering/             # Only when the domain has actual migrations
    DesignTime.Common/                     # Only when shared tooling is needed
    MigrationRunner/                       # Explicit migration execution
  Orchestration/
    Product.AppHost/                       # Optional local orchestration executable
```

Create only projects and folders with actual responsibilities. A role is already an application-layer boundary; do not add an `Application/` wrapper inside `Roles.*`. A specialized role needs distinct application responsibility, ingress, autonomous work, integration, or scaling. An entity, CRUD feature, page, or handler does not by itself require another role or Monica module. A second executable does not move its role out of the owning domain.

| Project | Responsibility and references |
| --- | --- |
| `Platform.Protocol` | Published requests, DTOs, events, stable role IDs, existing domain identifiers, and generated API interfaces. No domain implementations or deployment addresses. |
| `Platform.ApplicationCatalog` | Generated domain, role and published-operation ownership without runtime implementation dependencies. References Protocol normally and implementation projects as build-only metadata inputs. |
| `Domains.{Domain}` | Domain rules and owned persistence, including repositories and contexts. May use Protocol and BuildingBlocks; never references role implementations or peer domain implementations. |
| `Roles.{Domain}[.{Purpose}]` | Application handlers and role-owned runtime integrations. References its own domain core and shared contracts; never references peer roles or peer domain implementations. |
| `Platform.BuildingBlocks` | Shared technical mechanisms beneath Protocol. Does not reference Protocol or aggregate concrete business domains. |
| `Platform.Hosting` | Product configuration, provider defaults, and authority policies. Does not reference every domain or replace the native host lifecycle. |
| `Product.Host`, justified specialized hosts | Native composition of their actual local implementations, generated clients and local receivers. May reference the lightweight catalog and optional UI. |
| `Product.UI` | Layout, pages and presentation state. References Protocol and selected UI capabilities; never repositories, handlers, domain cores, or Platform.Hosting. |
| `Migrations.Domain{Domain}` | The domain's real migrations, snapshots and design-time factories. References its core and migration helpers, never roles or the business host. |

Application collaboration uses generated published APIs. Shared same-domain invariants belong in the core and are directly reusable by its roles. Technical cross-domain coordination uses narrow owned technical contracts where user-command semantics do not fit. Moving foreign repositories behind a service locator does not preserve the boundary.

## Ownership, artifact composition and compiled metadata

A domain is a business and data owner. A role is one domain-owned application responsibility. An artifact is a compiled dependency composition. An AppId is a logical deployment application shared by replicas, while an InstanceId identifies a process. These concepts are independent: one artifact can run under several AppIds, and a product can deploy different artifacts. Existing node identity, authority and primary/secondary policy remain independent of role selection.

Reuse the product's existing domain identifiers; do not add a second independently maintained domain registry. Declare stable role constants in Protocol. Dotted role names are exact identifiers: `ordering` does not enable `ordering.reporting`. Project, artifact or class renames do not rename these identifiers.

Configure domain display names and descriptions once through Core's [shared domain catalog](../../monica-infra-hosting/references/domain-catalog.md). ApplicationModel contributes compiled identities; infrastructure, UI, and diagnostic consumers share `IDomainCatalog`. Keep domain metadata separate from assembly ownership, deployment placement, and observed instances. Generated [API usage evidence](../../monica-infra-hosting/references/application-model.md#compiled-api-dependency-evidence) describes source-to-target dependencies independently of host placement; a host's loaded assemblies or available API clients do not imply dependencies on every domain.

Declare ownership once in each implementation assembly:

```csharp
// Domains.Ordering/Registration/DomainMetadata.cs
[assembly: Monica.ApplicationModel.Annotations.ApplicationDomain(ProductDomains.Ordering)]

// Roles.Ordering.Reporting/Registration/RoleMetadata.cs
[assembly: Monica.ApplicationModel.Annotations.ApplicationRole(ProductRoles.OrderingReporting, ProductDomains.Ordering)]

// Each business host's ApiGenerationConfig.cs
[assembly: Monica.ApplicationModel.Annotations.ApplicationHost]
```

These are separate source files in separate projects. `ProductDomains` and `ProductRoles` denote product-owned identifiers. One role implementation assembly has one owner; independently activated responsibilities use separate role projects. Protocol generates interfaces first, and role libraries and UI compile against those interfaces. Do not maintain repeated handler-marker lists or handwritten clients per role.

For selective hosts, generate a lightweight `Platform.ApplicationCatalog` from the annotated implementation libraries. Set `MonicaApplicationCatalog` to `true` in that project, reference Protocol normally, and use implementation `ProjectReference` entries with `ReferenceOutputAssembly="false"`, `Private="false"`, and `PrivateAssets="all"`. Add the Monica API generator analyzer to the annotated core and role projects so they export their owned metadata. Import `Monica.Generators.AutoController/build/Monica.ApplicationCatalog.targets` when consuming the generator from source; NuGet imports the packaged `build/Monica.Generators.AutoController.targets` automatically. See [application model composition](../../monica-infra-hosting/references/application-model.md) for the build and runtime boundary.

Each host references the catalog normally and only the implementations it can execute. Its generated manifest combines catalog ownership with `LocalAssemblies` from actual annotated implementation references. Published clients can describe remote operations without referencing their handler types; receivers and private operations come only from local implementations. The catalog contains no runtime role/core type references. Build-only metadata inputs may still require those projects to build; they must not copy their runtime or native assets into a host that omits them. Do not hand-edit metadata snapshots or introduce runtime assembly loading to satisfy missing implementations.

Compose the native `WebApplication` lifecycle with `builder.AddMonica(...)`, `app.UseMonica()`, and `app.MapMonica()`. Register `AddApplicationModel(options => options.Deployments = deployments)` and `AddApplicationInvocation()` in the callback, plus `AddWebApi()` where receivers are served. Set the existing current-host AppId through `ConfigureApplication`. A custom callback runner is not another application lifecycle.

## Deployment and active resources

The deployment map assigns each known product role to exactly one logical application in the selected scope. Replicas share that placement. For example:

```csharp
var deployments = new Dictionary<string, Monica.ApplicationModel.Models.ApplicationPlacement>
{
    ["product-api"] = new() { Roles = [ProductRoles.Ordering] },
    ["product-reporting"] = new() { Roles = [ProductRoles.OrderingReporting] }
};
```

A combined deployment assigns several included role IDs to one AppId. Split placement can run the same artifact under several AppIds or use justified specialized artifacts. There is no independently configured current-role list or synthetic `all` role. Startup validates ownership, unique placement, the current AppId, and local availability of every role assigned to that AppId. A role known through the catalog can be placed remotely without its implementation being installed locally. An omitted implementation cannot be activated by configuration.

Project references own artifact composition; deployment values own AppId/role placement and the image used for each workload. Preserve this distinction instead of adding arbitrary profile manifests or another artifact identity registry. A fully combined process must use an artifact that actually contains all selected implementations and their dependencies. Do not create an additional combined executable unless that deployment is needed. Role placement is frozen until restart. Runtime discovery adds live instances and addresses; it does not redefine ownership or install code.

Included ordinary handlers, passive services, repositories and in-operation local event behavior remain available locally even when their owning role is inactive. Local calls need their actual passive dependencies and database credentials. A selective host need not carry stores or native dependencies solely for an omitted remote implementation. Role selection is not a database access-security boundary.

Autonomous jobs, externally consumed subscriptions, hosted services and process-owned resources run only for selected, locally implemented roles. They inherit their role assembly's ownership. Explicit hubs, channels, Actors and connections must use the same validated selection before imperative registration. A local invariant event handler must remain distinct from an external subscriber. Do not register a resource everywhere and rely on an early return after it has started.

Durable workload ownership defaults to the stable role ID, independently of AppId. The optional `ApplicationRole.WorkloadOwner` preserves an already issued scheduler namespace during migration; it is not a new current-host identity. Role moves require controlled deployment takeover, compatible code/schema versions and preservation of durable job/subscriber identities. Provider leases and retries do not supply exactly-once business side effects.

## Calls and required execution location

Application invocation executes published operations through stable domain APIs. `Monica.Invocation` supplies generated API clients and Local/HTTP delivery. `Api` names the contract, `ApiClient` its consumer-side implementation, `Invoker` routes or executes calls, and `Caller` denotes the initiating identity/context. Local is in-process execution through the application boundary; HTTP is a delivery adapter. Deployment changes do not rename domain APIs or select a transport in business code. See [generation and registration](../../monica-infra-messaging/references/invocation.md) for the concrete types and module entrypoints.

Published requests remain in the exact `*.PublishedLanguages.Domain{Domain}.Requests` namespace. The single `ApiEndpoint(method, route)` owns HTTP metadata and generates stable `I{Domain}CommandApi` / `I{Domain}QueryApi` interfaces. Several roles may contribute operations to one domain interface. Splitting a role or artifact never creates another published domain or a separate interface merely to mirror its project name.

Generated clients apply product authority policy first, then required execution placement, then local availability. With no authority or execution constraint, an operation runs locally when its implementation is included, even when its role belongs to another AppId. When the implementation is omitted, the caller uses the known operation owner's configured AppId through HTTP. A missing local implementation is not permission to guess a domain-wide owner or silently skip the operation.

Required execution placement remains exceptional:

```csharp
[ApiEndpoint(ApiHttpMethod.Post, "messages/send")]
[Monica.ApplicationModel.Annotations.RunOnRole(ProductRoles.MessageConnection)]
public sealed record CommandSendMessage(Guid MessageId) : IResultRequest;
```

Use this only when execution needs the role's live resources or ownership, not to force remote calls merely because another artifact omits a handler. Persisting a message can remain ordinary local work while sending over an owned socket requires that role. The request's required role must agree with its handler's owner. The generated client executes such an operation locally only when its implementation is present and its required role is active; otherwise it calls the configured owner. Remote failure never falls back locally. Direct mediator execution does not provide remote routing and cannot bypass a required role.

An outer Local call creates a fresh operation scope; nested Local calls reuse it. A remote call creates a separate receiving operation. Trusted caller identity, authorization and cancellation remain part of the application boundary. Authority policy must retain its semantics for remote-only operations through trusted compiled metadata, without forcing the caller to load remote handler assemblies.

HTTP ingress requires both a local implementation and active handler-role ownership. Required receivers exist in Release builds; optional diagnostic publication is a separate exposure choice. A receiver executes locally and never forwards recursively. Private multipart, file export and integration endpoints can remain in their owning role and retain their existing gateway route; they do not become serializable published APIs automatically. The build-generated catalog is not a general remote-manifest import or runtime plugin system. Existing explicit HTTP client generation remains available. gRPC is unsupported without a real client/receiver adapter and serialization contract.

## Persistence and migration ownership

Operations select persistence through their owning domain, never through current AppId, artifact name or selected role list. Domain roles share that domain's stores. A domain may own current/history contexts or several stores; a new role or executable does not create another database.

Keep actual migration projects at top-level `src/Migrations/Migrations.Domain{Domain}`. The domain owns all its context factories, migration chains and snapshots in that project. Preserve issued migration IDs, context identities, migration assembly names, history settings and shared-table ownership. Current/history chains remain separate even when they share the migration assembly. Do not create empty migration projects or one migration project per role.

Design-time factories compose persistence without starting roles, jobs, channels or subscribers. MigrationRunner performs explicit selected migrations with preflight, ordering and dry-run behavior where adopted. Business replicas do not automatically apply schema migrations. Framework store migrations retain their established owners.

Co-location does not make independent stores atomic. A shared local transaction requires actual provider participation; current Monica shared-connection participation requires the same DbConnection instance. Nested cross-domain work must not silently switch or independently commit another domain's context. Use an explicitly non-atomic coordination or messaging design where stores cannot participate together.

The [reference application](../../../examples/Monica.ReferenceApplication/README.md) demonstrates a host containing both Ordering roles, ordinary inactive-role local calls, and role-gated reporting with the existing business UI. Its in-memory repository does not demonstrate distributed persistence or transactional rollback.
