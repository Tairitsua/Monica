# Microservice Solution Layout

Use this structure when a Monica business solution is intentionally split into independently owned subdomain services.

The dependency chain and ownership rules are mandatory within this profile. The tree illustrates their physical layout; create only the folders used by actual units. The project's application specification records concrete topology, names, and approved deviations.

```text
src/
├── AppHost/                                  # Optional orchestration/composition entry points
│   └── Gateway/
│       ├── Gateway.csproj
│       ├── Program.cs                        # Composition root only
│       └── Modules/                          # Optional composition modules; no business units
├── Shared/
│   ├── Platform.BuildingBlocks/
│   │   └── Platform.BuildingBlocks.csproj    # Project-agnostic infrastructure building blocks and third-party extensions
│   ├── Platform.Infrastructure/
│   │   └── Platform.Infrastructure.csproj    # Solution-owned infrastructure setup and integration wiring
│   └── Platform.Protocol/
│       ├── Platform.Protocol.csproj
│       ├── WebApiGeneratorConfig.cs          # Published request route prefix and RPC targets
│       └── PublishedLanguages/
│           └── Domain{Subdomain}/
│               ├── Requests/                 # Shared request contracts
│               ├── Models/                   # Shared DTOs and enums
│               ├── Events/                   # Shared event contracts
│               ├── Contracts/                # Optional checked-in synchronous service contracts
│               └── Implementations/
│                   ├── Http/                 # Optional wrappers or adapters around generated HTTP RPC clients
│                   └── Local/                # Optional checked-in local or actor-backed providers
├── Services/
│   └── {Subdomain}/
│       ├── {Subdomain}Service.API/
│       │   ├── Program.cs                    # Service entry point and local WebApiGenerationConfig
│       │   ├── HandlersCommand/              # ApplicationService units
│       │   ├── HandlersQuery/                # ApplicationService units
│       │   ├── HandlersEvent/                # Event handler units
│       │   └── BackgroundWorkers/            # Job units
│       ├── {Subdomain}Service.Domain/
│       │   ├── Entities/
│       │   ├── ValueObjects/
│       │   ├── DomainServices/
│       │   ├── Events/
│       │   ├── Interfaces/
│       │   ├── Configurations/
│       │   ├── Utilities/                   # Pure helper code, named `Utils*`
│       │   └── Repository/                  # Repository implementations, DbContext, and EF mapping
└── Migrations/
    └── {Subdomain}/                          # Migration project per service
```

Recommended `.slnx` folders should mirror the physical layout:

```text
/src/AppHost/
/src/Shared/
/src/Services/
/src/Migrations/
```

## Mapping Rules

- For shared specialization naming, mechanism choice, and capability placement, follow the [project-level extension rules](../../monica-application-project-unit-development/references/01-project-unit-naming-and-boundaries.md#project-level-extensions) within this topology.
- Keep the same shared platform split across solution styles to reduce learning overhead.
- For applications with UI, adopt the [presentation branch](../../monica-application-ui/references/presentation-specification.md): `AppHost -> Presentation -> Platform.Protocol`. A host may compose presentation alongside its backend; a remote presentation host must not reference remote service implementations. Product UI libraries reference no domain implementation or Platform.Infrastructure.
- Use the strict backend solution-project reference chain `{Subdomain}Service.API -> {Subdomain}Service.Domain -> Platform.Infrastructure -> Platform.Protocol -> Platform.BuildingBlocks`.
- Put project-agnostic infrastructure extensions into `Shared/Platform.BuildingBlocks/Platform.BuildingBlocks.csproj`.
- Put solution-owned infrastructure composition and integration configuration into `Shared/Platform.Infrastructure/Platform.Infrastructure.csproj`.
- Put cross-service contracts in `Shared/Platform.Protocol/PublishedLanguages`, not inside a service project. Only attributed `Command*` and `Query*` requests are generated RPC endpoints.
- Put business implementation in the service's own `API` and `Domain` projects.
- Keep service-only package references in `{Subdomain}Service.Domain`. Do not add extra shared-project references just to reach them.
- Put `ApplicationService`, event-handler, and background-worker ProjectUnits in the service `API` project under `HandlersCommand`, `HandlersQuery`, `HandlersEvent`, and `BackgroundWorkers`.
- Put the local-request `WebApiGenerationConfig("api/v1", DomainName = "{Subdomain}")` in `{Subdomain}Service.API/Program.cs`, not in a shared gateway project.
- Put the published-request `WebApiGenerationConfig` in `Platform.Protocol`, including the intended `RpcClientTargets` and transport base types. Do not create or consume `RpcMetadata` JSON.
- Keep `DomainServices/` as the standard folder for `DomainService` units.
- Keep `Utilities/` in the `Domain` project for pure helpers and name them `Utils*`.
- Do not create `Persistence/` or `Providers/` as default folders. Keep repository implementations, `DbContext`, and EF mapping in `Domain/Repository/`.
- Put migrations beside the service boundary they belong to, not in a global dump folder.
- Keep AppHost or gateway projects composition-only. `Program.cs`, composition modules, registration extensions, and thin host adapters are allowed; business ProjectUnits and domain behavior remain in their owning service.
- Keep `.slnx` folders aligned with the real `src/` tree so the solution view reflects AppHost, shared platform, services, and migrations ownership correctly.
