# Unified application architecture

Use one development model for combined and split deployments. Business ownership belongs to domains, application responsibilities belong to domain-owned roles, and project references determine which implementations a host artifact contains. Deployment configuration places roles under logical AppIds. A normal host may compose most roles while a separate host carries OCR, GPU, native, licensed, or otherwise incompatible runtime dependencies. Create another host for a real dependency or runtime composition need, not for every role or AppId.

## Physical ownership

```text
src/
  Shared/
    Platform.Protocol/
      Application/                         # Stable role IDs and existing domain identifiers
      PublishedLanguages/DomainOrdering/{Requests,Models,Events}/
    Platform.Infrastructure/               # Product technical code reused by multiple domain/role owners
    Platform.BuildingBlocks/               # Optional product-independent, Monica-independent library
    Platform.Testing/                      # Optional shared test support; no test cases
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
      Composition/                         # Whole-host modules, providers, UI, middleware and endpoints
    Product.Ocr.Host/                      # Only when separate native dependencies are needed
    Product.ApplicationCatalog/            # Optional generated ownership catalog for selective hosts
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
| `Platform.Protocol` | Published requests, DTOs, events, stable role IDs, existing domain identifiers, and generated API interfaces. May use independent BuildingBlocks values and Monica contract/annotation APIs. No implementations, persistence bases, options, provider/UI setup, deployment addresses or environment files. |
| `Platform.Infrastructure` | Product-specific technical code on Monica reused by multiple domains or roles: adapters, persistence bases and technical policies/contracts. May reference Protocol, BuildingBlocks and the Monica capabilities it uses; never domain cores, roles or hosts. |
| `Platform.BuildingBlocks` | Optional code that could ship independently of this product and Monica, such as coordinate mathematics. May use third-party libraries; references no Monica or other Platform project. |
| `Platform.Testing` | Optional shared fixtures, factories and doubles. May reference production projects and Monica.Testing. Contains no test cases and is never referenced by production. |
| `Domains.{Domain}` | Domain rules and owned persistence, including repositories and contexts. May use Infrastructure, Protocol and BuildingBlocks; never references role implementations or peer domain implementations. |
| `Roles.{Domain}[.{Purpose}]` | Application handlers and role-owned runtime integrations. References its own domain core and shared contracts; never references peer roles or peer domain implementations. |
| `Product.Host`, justified specialized hosts | Native composition of their actual local implementations, generated clients and local receivers. Whole-host module/provider choices, UI shell, middleware and product endpoints stay in the host's Composition folder. May reference an optional generated catalog and UI. |
| `Product.ApplicationCatalog` | Optional host build artifact containing generated domain, role and operation ownership without runtime implementation dependencies. References Protocol normally and implementations as build-only metadata inputs. It is not a Platform responsibility. |
| `Product.UI` | Layout, pages and presentation state. References Protocol and selected UI capabilities; never repositories, handlers, domain cores, or host composition. |
| `Migrations.Domain{Domain}` | The domain's real migrations, snapshots and design-time factories. References its core and migration helpers, never roles or the business host. |

Application collaboration uses generated published APIs. Shared same-domain invariants belong in the core and are directly reusable by its roles. Technical cross-domain coordination uses narrow owned technical contracts where user-command semantics do not fit. Moving foreign repositories behind a service locator does not preserve the boundary.

The four Platform names describe allowed responsibilities, not four mandatory projects. Keep code with its only consumer until it has actual shared owners. Monica-based code useful to unrelated products belongs in Monica rather than in BuildingBlocks. Infrastructure does not select the entire host's modules or become a dependency barrel that makes Protocol transitively import runtime providers and UI. Reusable technical code does not justify moving one domain's repository into Platform.

Production dependency direction is Host → Roles → own Domain → Infrastructure → Protocol → BuildingBlocks; references may skip levels downward. No production library references a host or testing library, and domain cores never reference one another. Enforce those boundaries with focused project-reference checks, including a Protocol dependency allow-list and absence of copied environment files. Shared test support and executable tests follow [the production behavior owner](../../monica-application-unit-testing/references/project-naming.md).

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

When hosts include different implementation sets, generate an optional lightweight catalog under `src/AppHost/{Product}.ApplicationCatalog` from the annotated implementation libraries. A host that already includes every implementation needs no separate catalog project. Set `MonicaApplicationCatalog` to `true` in the catalog, reference Protocol normally, and use implementation `ProjectReference` entries with `ReferenceOutputAssembly="false"`, `Private="false"`, and `PrivateAssets="all"`. Add the Monica API generator analyzer to annotated core and role projects so they export their owned metadata. Import `Monica.Generators.AutoController/build/Monica.ApplicationCatalog.targets` when consuming the generator from source; NuGet imports the packaged `build/Monica.Generators.AutoController.targets` automatically. See [application model composition](../../monica-infra-hosting/references/application-model.md) for the build and runtime boundary.

Each selective host references that catalog normally and only the implementations it can execute. Its generated manifest combines catalog ownership with `LocalAssemblies` from actual annotated implementation references. Published clients can describe remote operations without referencing their handler types; receivers and private operations come only from local implementations. The catalog contains no runtime role/core type references. Build-only metadata inputs may still require those projects to build; they must not copy their runtime or native assets into a host that omits them. Do not hand-edit metadata snapshots or introduce runtime assembly loading to satisfy missing implementations.

Compose the native `WebApplication` lifecycle with `builder.AddMonica(...)`, `app.UseMonica()`, and `app.MapMonica()`. Register `AddApplicationModel()` and `AddApplicationInvocation()` in the callback, plus `AddWebApi()` where receivers are served. The canonical `Application` section owns current AppId and deployment placement. Keep product/assembly information such as ProjectName and AppVersion in `ConfigureApplication`. A complete typed `ModuleApplicationModelOption.Deployments` value is an alternative to file topology; supplying both fails instead of merging authorities. A custom callback runner is not another application lifecycle.

## Deployment and active resources

The deployment map assigns each known product role to exactly one logical application in the selected scope. Replicas share that placement. For example:

```json
{
  "Application": {
    "AppId": "product-api",
    "DeploymentScope": "product-uat",
    "AutonomousWork": true,
    "Deployments": {
      "product-api": { "AppName": "Ordering API", "Roles": ["ordering"] },
      "product-reporting": { "AppName": "Ordering reports", "Roles": ["ordering.reporting"] }
    }
  }
}
```

A combined deployment assigns several included role IDs to one AppId. Split placement can run the same artifact under several AppIds or use justified specialized artifacts. There is no independently configured current-role list, default role owner or synthetic `all` role. Every compiled role has exactly one explicit owner. Startup rejects missing/unknown/duplicate roles, case-ambiguous identities, an absent current AppId and missing local implementations for roles assigned to that AppId. Every application entry declares Roles; an explicit empty array is valid, omission or null is not. A role known through the catalog can be placed remotely without its implementation being installed locally. An omitted implementation cannot be activated by configuration. DeploymentScope supplies the default scheduler scope and telemetry deployment environment; preserve already issued resource keys when adopting it.

AppName belongs to the same deployment entry and defaults to its AppId. Core identity, discovery and diagnostics use that name; conflicting direct identity or discovery overrides fail. The framework derives the reverse role-to-AppId index. `Deployments` is selected as one complete subtree from the highest-priority provider that explicitly supplies it. A higher source replaces all lower applications, names and role arrays. Partial environment-variable/command-line maps fail when incomplete; they do not inherit missing roles. Use JSON for complete topology and scalar environment variables for current AppId. Private environment JSON selection follows the [host input contract](../../monica-infra-hosting/references/host-composition.md); selected inputs and placement remain frozen until restart.

Project references own artifact composition; deployment values own AppId/role placement and the image used for each workload. Preserve this distinction instead of adding arbitrary profile manifests or another artifact identity registry. A fully combined process must use an artifact that actually contains all selected implementations and their dependencies. Do not create an additional combined executable unless that deployment is needed. Role placement is frozen until restart. Runtime discovery adds live instances and addresses; it does not redefine ownership or install code.

Included ordinary handlers, passive services, repositories and in-operation local event behavior remain available locally even when their owning role is inactive. Local calls need their actual passive dependencies and database credentials. A selective host need not carry stores or native dependencies solely for an omitted remote implementation. Role selection is not a database access-security boundary.

Role-owned autonomous jobs, external subscriptions, hosted services and shared resources use the same validated placement. `Application:AutonomousWork` defaults to true; false keeps eligible explicit execution available while holding unsolicited participation. Monica derives Disabled, Manual or Automatic for each resource. A domain core declares a same-domain BackgroundRole only when it owns autonomous resources; passive behavior requires no such assignment. Work belonging to several roles keeps its actual role entry points rather than relabeling them with one core default. A local invariant event handler remains distinct from an external subscriber.

Framework modules retain registration provenance across deferred callbacks and factories, and decide activation per resource. A shared framework worker does not own every store it executes. Outbox delivery is anchored to its DbContext's owner; another role's callback cannot claim that context. Narrow process-memory initialization can use `[ProcessLocal]` under the [startup and local execution contract](04-startup-and-local-execution.md). That contract also owns the Manual capability boundaries, readiness and explicit shared-write implications. Applications maintain no second role list or generic manual execution engine.

Durable workload ownership defaults to the stable role ID, independently of AppId. The optional `ApplicationRole.WorkloadOwner` preserves an already issued scheduler namespace during migration; it is not a new current-host identity. Role moves require controlled deployment takeover, compatible code/schema versions and preservation of durable job/subscriber identities. Provider leases and retries do not supply exactly-once business side effects.

## Native domain and role registration

An included implementation can name one concrete `ApplicationRegistration` on its existing assembly declaration:

```csharp
// Roles.Ordering.Reporting/Registration/RoleMetadata.cs
[assembly: ApplicationRole(ProductRoles.OrderingReporting, ProductDomains.Ordering,
    Registration = typeof(RoleRegistration))]

public sealed class RoleRegistration : ApplicationRegistration
{
    public override void ConfigureActive(ApplicationRegistrationContext context)
    {
        context.Monica.AddJobScheduler().UseInMemoryStore();
    }
}
```

The registration and context are in `Monica.ApplicationModel.Abstractions`; the assembly annotation is in `Monica.ApplicationModel.Annotations`. The registration belongs to its declaring assembly, is concrete and has a public parameterless constructor. A domain uses `ApplicationDomain.Registration` the same way. Its optional BackgroundRole must name a declared role in that domain; a domain overriding ConfigureActive must supply that owner. A passive-only domain needs no BackgroundRole.

After the host's AddMonica callback finishes, Monica finalizes application-model contributions once, validates the complete model and projects identity. It freezes identity, discovery and topology before running registrations. All included registrations run ConfigurePassive first, followed by eligible ConfigureActive callbacks in deterministic owner-ID order. Passive registration is independent of role placement. Active registration requires local placement of the role or the domain's BackgroundRole. Catalog-only remote owners never execute registration code.

Callbacks can add modules, service descriptors and read Startup options; they cannot change frozen inputs, resolve services, open connections, start tasks or depend on another owner's callback order. `context.Configuration` is the immutable startup snapshot, and `context.GetStartupOptions<T>()` uses the configuration module's shared early reader when imported from `Monica.Configuration.Bootstrap`. ConfigureActive runs for locally placed owners in both Manual and Automatic modes so native modules can retain their definitions. Use `context.IsAutonomous` to guard imperative provider registrations that would otherwise join shared resources. Module declarations then close and the normal module lifecycle proceeds. The framework retains registration ownership across deferred module contributions and service descriptors; products maintain no additional owner table.

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

The [reference application](../../../examples/Monica.ReferenceApplication/README.md) demonstrates host-owned composition, both Ordering roles, the local Manual profile and native job execution with the existing business UI. Its in-memory repository does not demonstrate distributed persistence or transactional rollback. Use [startup and local execution](04-startup-and-local-execution.md) when adapting its deployment inputs or local profile.
