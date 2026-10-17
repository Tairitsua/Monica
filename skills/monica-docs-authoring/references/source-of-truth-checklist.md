# Verify Monica documentation claims

Inspect the owning project's `.csproj` for package identity and its `Modules/Module{Name}.cs` for public registration, module dependencies, option defaults, and required features. For the relevant claim, inspect public `Abstractions/`, `Annotations/`, `Models/`, `Facades/`, `Events/`, `Exceptions/`, and extensions. Check any paired UI module separately.

For generated Web API or RPC guidance, inspect source requests carrying `[ApiEndpoint]`, shared HTTP mapping, host ApplicationApiGenerationConfig and optional WebApiGenerationConfig, and the request namespace. Attributed requests outside `*.PublishedLanguages.Domain{DomainName}.Requests` remain local HTTP contracts. Generated-source tests can establish exact generated names when those names matter.

Check a candidate example against current source and, when useful, a compiling sample or test. Existing docs and README text are secondary evidence. Before delivery, confirm registration names, defaults, required setup, package identity, request publication boundary, destination path, and any locale counterpart affected by a public contract change.
