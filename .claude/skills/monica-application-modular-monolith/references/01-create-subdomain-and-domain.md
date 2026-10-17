# Create a New Subdomain Domain Package

Use this workflow when a new bounded context should live inside the same deployable application.

## Step 1. Name the bounded context

- Choose the business name first, such as `Ordering`, `Billing`, or `Warehouse`.
- Use that name consistently for the shared language area and the merged domain package:
  - `Domain{Subdomain}` in `Platform.Protocol/PublishedLanguages`
  - `Domains.{Subdomain}.csproj`

- If the solution does not already have a suitable entry project, create one with an explicit solution name, preferably ending with `Api`, such as `Monica.Docs.Api`.

## Step 2. Create the protocol language area

Create:

```text
src/Shared/Platform.Protocol/PublishedLanguages/Domain{Subdomain}/
├── Requests/
├── Models/
├── Events/
├── Contracts/        # only if checked-in synchronous domain-to-domain contracts are required
└── Implementations/
    ├── Http/         # only if wrapping or extending the generated HTTP RPC clients
    └── Local/        # only if local or actor-backed providers are shared intentionally
```

Rules:

- Put only stable cross-domain requests, DTOs, enums, events, and optional shared `Contracts/` or `Implementations/*` surfaces here.
- Add `[ApiEndpoint]` only to published `Command*` or `Query*` requests that should become HTTP/RPC operations. Leave other shared language unattributed.
- Add the analyzer to Protocol for stable interfaces. Select caller adapters with ApplicationApiGenerationConfig in the host, not Protocol. See [RPC generation](../../monica-infra-messaging/references/rpc.md).
- Keep persistence entities and repositories out of `Platform.Protocol`.

## Step 3. Create the domain package

Create:

```text
src/Domains/{Subdomain}/
├── Domains.{Subdomain}.csproj
├── WebApiGeneratorConfig.cs
├── Application/
│   ├── HandlersCommand/
│   ├── HandlersQuery/
│   ├── HandlersEvent/
│   └── BackgroundWorkers/
├── Interfaces/
├── DomainServices/
├── Configurations/
├── Utilities/
└── Repository/
```

Use `monica-application-project-unit-development` to place domain-owned units inside this project:

- `ApplicationService` units go under `Application/HandlersCommand/` or `Application/HandlersQuery/`.
- Event handlers go under `Application/HandlersEvent/`.
- Jobs go under `Application/BackgroundWorkers/`.
- `DomainService` units go under `DomainServices/`.
- Pure helpers go under `Utilities/` and use `Utils*` names.
- Keep repository implementations, `DbContext`, and EF mapping in `Repository/`.
- Keep private HTTP request wire settings in HttpApiContract with the owning DomainName. Configure receiver publication in the host and select referenced handler assemblies explicitly.
- Co-locate domain-private endpoint requests and responses with their handlers by default. Put `[ApiEndpoint]` and the XML summary on the request; do not put endpoint metadata on the handler.

## Step 4. Keep AppHost composition-only

Create or extend an AppHost entry project:

```text
src/AppHost/{ProjectName}/
├── {ProjectName}.csproj
├── Program.cs
└── Modules/          # optional composition modules
```

Keep AppHost as the composition root. Modules, registration extensions, and thin host adapters may organize wiring; handlers, jobs, repositories, business helpers, and other domain behavior remain with their owner.

## Step 5. Register the domain and update the solution

- Add domain registration and host composition in `Program.cs`.
- Keep the local-request generation config inside the domain project root, not in AppHost `Program.cs`.
- Register `monica.AddConfiguration()` in the AppHost's `builder.AddMonica(...)` callback. If later module options need bootstrap values, read them directly from `builder.Configuration`; do not resolve runtime Configuration ProjectUnits during composition.
- Keep `.slnx` folders aligned with `src/AppHost`, `src/Shared`, and `src/Domains`.

## Step 6. Add persistence ownership

- Add or extend the domain's `Repository` area and the shared migrator entry point.
- Keep ownership explicit even though deployment is shared.

## Decision Rule

Create a new domain package only when the subdomain has distinct language, invariants, and ownership. Do not create one for every page or CRUD screen.
