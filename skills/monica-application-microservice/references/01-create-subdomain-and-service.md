# Create a New Subdomain Service

Use this workflow when adding a new business capability that deserves its own service boundary.

## Step 1. Name the subdomain

- Choose a business name first, such as `Ordering`, `Billing`, or `Warehouse`.
- Use that name consistently for:
  - `Domain{Subdomain}` in `Platform.Protocol/PublishedLanguages`
  - `{Subdomain}Service.API`
  - `{Subdomain}Service.Domain`
  - `Migrations/{Subdomain}`

## Step 2. Create the shared contract area

Create:

```text
src/Shared/Platform.Protocol/PublishedLanguages/Domain{Subdomain}/
├── Requests/
├── Models/
├── Events/
├── Contracts/        # only if checked-in synchronous service-to-service contracts are required
└── Implementations/
    ├── Http/         # only if wrapping or extending the generated HTTP RPC clients
    └── Local/        # only if local or actor-backed providers are shared intentionally
```

Rules:

- Put only stable cross-service contracts here.
- Add `[ApiEndpoint]` only to `Command*` or `Query*` requests that are callable endpoints. Shared DTOs, events, and non-endpoint language stay unattributed.
- Add the analyzer to Protocol for stable interfaces. Select caller adapters with ApplicationApiGenerationConfig in the host, not Protocol. See [RPC generation](../../monica-infra-messaging/references/rpc.md).
- Put checked-in synchronous abstractions in `Contracts/`.
- Put checked-in shared providers in `Implementations/Local/` or deliberate wrappers in `Implementations/Http/`.
- Do not copy entity types into this area.

## Step 3. Create the service pair

Create:

```text
src/Services/{Subdomain}/
├── {Subdomain}Service.API/
│   ├── Program.cs
│   ├── HandlersCommand/
│   ├── HandlersQuery/
│   ├── HandlersEvent/
│   └── BackgroundWorkers/
├── {Subdomain}Service.Domain/
│   ├── Entities/
│   ├── ValueObjects/
│   ├── DomainServices/
│   ├── Events/
│   ├── Interfaces/
│   ├── Configurations/
│   ├── Utilities/
│   └── Repository/
```

Use `monica-application-project-unit-development` to fill the correct ProjectUnits inside those projects.

- Keep pure helpers in `{Subdomain}Service.Domain/Utilities/` and name them `Utils*`.
- Keep repository implementations, `DbContext`, and EF mapping in `{Subdomain}Service.Domain/Repository/`.
- Use the strict solution-project reference chain `{Subdomain}Service.API -> {Subdomain}Service.Domain -> Platform.Infrastructure -> Platform.Protocol -> Platform.BuildingBlocks`.
- Keep private HTTP request wire settings in HttpApiContract with the owning DomainName. Configure receiver publication in the host and select referenced handler assemblies explicitly.
- Co-locate a service-private request and response with its handler by default. Give the request `[ApiEndpoint]`; keep all HTTP verb, route, binding, and XML-summary metadata off the handler.

## Step 4. Add persistence ownership

- If the new service owns data, create or extend its migration project under `src/Migrations/{Subdomain}/`.
- Keep each service responsible for its own persistence model and migrations.

## Step 5. Wire the host and update the solution

- Register the service in the solution's host or gateway `Program.cs`.
- Keep the service's local-request generation config in `{Subdomain}Service.API/Program.cs`, even if the solution also has a gateway or AppHost.
- Register `monica.AddConfiguration()` in the service host's `builder.AddMonica(...)` callback. If later module options need bootstrap values, read them directly from `builder.Configuration`; do not resolve runtime Configuration ProjectUnits during composition.
- Keep orchestration metadata outside the domain projects.
- Keep `.slnx` folders aligned with `src/AppHost`, `src/Shared`, `src/Services`, and `src/Migrations`.

## Decision Rule

Create a new service only when the subdomain owns distinct business capability and data. Do not create a service just because a new HTTP endpoint appeared.
