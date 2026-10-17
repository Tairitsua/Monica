# Modular Monolith Solution Layout

Use this structure when the business solution is one deployment but still needs explicit DDD subdomain boundaries.

The dependency chain and ownership rules are mandatory within this profile. The tree illustrates their physical layout; create only the folders used by actual units. The project's application specification records concrete topology, names, and approved deviations.

```text
src/
├── AppHost/
│   └── {ProjectName}/
│       ├── {ProjectName}.csproj              # Entry point, prefer explicit names such as Monica.Docs.Api
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
│               ├── Models/                   # Stable DTOs and enums
│               ├── Events/                   # Cross-domain event contracts
│               ├── Contracts/                # Optional checked-in synchronous domain contracts
│               └── Implementations/
│                   ├── Http/                 # Optional wrappers or adapters around generated HTTP RPC clients
│                   └── Local/                # Optional checked-in local or actor-backed providers
├── Domains/
│   └── {Subdomain}/
│       ├── Domains.{Subdomain}.csproj        # Single domain package project
│       ├── WebApiGeneratorConfig.cs          # Assembly-level config for local endpoint requests
│       ├── Application/                      # Domain-owned use cases and background workflows
│       │   ├── HandlersCommand/              # ApplicationService command units
│       │   ├── HandlersQuery/                # ApplicationService query units
│       │   ├── HandlersEvent/                # Event handler units
│       │   └── BackgroundWorkers/            # Job units
│       ├── Entities/
│       ├── ValueObjects/
│       ├── Events/
│       ├── Interfaces/
│       ├── DomainServices/
│       ├── Configurations/
│       ├── Utilities/                       # Pure helper code, named `Utils*`
│       └── Repository/                      # Repository implementations, DbContext, and EF mapping
└── Database/
    └── DbMigrator/
        └── DbMigrator.csproj
```

Recommended `.slnx` folders should mirror the physical layout:

```text
/src/AppHost/
/src/Shared/
/src/Domains/
```

## Mapping Rules

- For shared specialization naming, mechanism choice, and capability placement, follow the [project-level extension rules](../../monica-application-project-unit-development/references/01-project-unit-naming-and-boundaries.md#project-level-extensions) within this topology.
- Split by subdomain under `Domains/` first. Do not create global `Application`, `Domain`, or `Infrastructure` buckets for the whole solution.
- Keep the same shared platform split across solution styles to reduce learning overhead.
- For applications with UI, adopt the [presentation branch](../../monica-application-ui/references/presentation-specification.md): `AppHost -> Presentation -> Platform.Protocol`. A host may compose presentation alongside its backend; a remote presentation host must not reference remote service implementations. Product UI libraries reference no domain implementation or Platform.Infrastructure.
- Use the strict backend solution-project reference chain `AppHost -> Domains.{Subdomain} -> Platform.Infrastructure -> Platform.Protocol -> Platform.BuildingBlocks`.
- Put project-agnostic infrastructure extensions into `Shared/Platform.BuildingBlocks/Platform.BuildingBlocks.csproj`.
- Put solution-owned infrastructure composition and integration configuration into `Shared/Platform.Infrastructure/Platform.Infrastructure.csproj`.
- Keep `Shared/Platform.Protocol/PublishedLanguages` stable and referenceable from other domains. Only attributed `Command*` and `Query*` requests are RPC endpoints.
- Keep subdomain-specific package references in the owning `Domains.{Subdomain}.csproj`; do not introduce extra shared-project references just to reach them.
- Keep domain models, repositories, `DbContext` ownership, and helper code inside the owning `Domains.{Subdomain}.csproj`.
- Put application services, event handlers, and background workers in the owning domain package under `Application/HandlersCommand`, `Application/HandlersQuery`, `Application/HandlersEvent`, and `Application/BackgroundWorkers`.
- Keep private HTTP request wire settings in HttpApiContract with the owning DomainName. Configure receiver publication in the host and select referenced handler assemblies explicitly.
- Configure published RPC targets in the `Platform.Protocol` assembly and do not create `RpcMetadata` JSON or build export tasks.
- Keep `DomainServices/` as the standard folder for `DomainService` units.
- Keep `Utilities/` as the standard folder for pure helpers and name them `Utils*`.
- Do not create `Persistence/` or `Providers/` as default folders. Keep repository implementations, `DbContext`, and EF mapping in `Repository/`.
- Keep AppHost projects composition-only. `Program.cs`, composition modules, registration extensions, and thin host adapters are allowed; business ProjectUnits and domain behavior remain in their owning domain.
- Keep `.slnx` folders aligned with the real `src/` tree so the solution view matches the layout rules.
- Do not require `*.WebHost` names or an extra endpoint layer inside AppHost. Use `Modules/` when composition benefits from modules.
