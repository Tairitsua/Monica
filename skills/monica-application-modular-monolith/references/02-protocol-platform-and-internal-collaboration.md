# Published collaboration in a combined process

The [unified specification](../../monica-application/references/03-unified-architecture.md) owns project direction, identifiers and generation. Protocol owns requests, DTOs, events and generated domain API interfaces. Role libraries implement handlers; each Host compiles callers and a manifest combining shared ownership with its actual local implementation inventory. A specialized artifact can omit implementations while retaining their published ownership through the generated lightweight catalog.

Call published operations through I{Domain}CommandApi / I{Domain}QueryApi even in one process. Ordinary included operations remain locally callable when their role is inactive; omitted published operations route to their known remote owner. Do not substitute a peer domain repository or concrete handler call for that boundary. Shared same-domain invariants can be reused directly from the core.

Compose AddApplicationModel and AddApplicationInvocation. Local calls preserve trusted caller/tenant context: independent outer calls create fresh scopes, nested calls reuse the active operation and remote calls create another operation. Never retain scope-owned services or detach nested work.

ApiEndpoint preserves HTTP mapping for Local clients. Host publication selects owned receivers in Release; optional diagnostic exposure is separate. See [API](../../monica-infra-messaging/references/invocation.md) and [operation scopes](../../monica-application-ui/references/application-invocation.md).
