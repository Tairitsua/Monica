# Combined role deployment

Use the [unified domain/role layout](../../monica-application/references/03-unified-architecture.md). Assign intended, included roles to one logical AppId. This placement needs no alternative merged application/domain project structure. Heavy native or incompatible runtime dependencies can remain in a justified separate host; combining roles does not require every product role to inhabit one process.

The Host references domain cores, role libraries and optional product UI. Domain cores own rules and persistence. Role libraries own application handlers and integrations. Platform.Hosting supplies product bootstrap/provider defaults without replacing the native AddMonica, UseMonica and MapMonica lifecycle.

Ordinary published calls use Local when the implementation is included and authority/execution placement permits it; omitted implementations use their known remote owners. Autonomous work and ingress follow the selected role plan. Splitting placement among existing supported artifacts changes configuration and controlled deployment, not caller interfaces or project ownership. Combining a previously omitted role requires an artifact that includes it; configuration cannot add code or native dependencies.

Create only justified roles and real migration projects. Keep optional orchestration and explicit migration tools outside the business Host. Read [bounded-context migration](01-create-subdomain-and-domain.md) for an application with handlers still merged into Domains.*.
