# Split role deployment

Use the [unified application layout](../../monica-application/references/03-unified-architecture.md) for all new projects. The default business artifact can run under several AppIds. A justified OCR, GPU, native or otherwise distinct dependency composition may use a separate thin host. Domain cores and role libraries keep the same ownership and layout. Project references select available implementations; deployment placement selects autonomous work and HTTP ingress from the artifact's supported roles.

Assign each exact known role ID to one AppId in the selected deployment scope. Replicas use the same AppId and role set. Every selected role must have a local implementation in that artifact. Separate a role only when its responsibility, runtime integration, ingress or scaling needs justify the boundary. A new executable needs a distinct dependency/runtime composition, not merely another AppId. Do not add generic artifact profiles or a host per role.

Generate the lightweight shared catalog when artifacts omit implementations. Configure HTTP destinations for omitted published operations, required-role operations and product authority routing through the existing provider. Ordinary unconstrained calls prefer Local when their implementation is included, even across role ownership; otherwise they use the known operation owner's AppId. Role activation is fixed during startup; moving ownership requires a restart and controlled takeover.

The migration runner and optional local orchestration executable retain separate operational purposes. Migration projects remain top-level and domain-owned. Read [existing service migration](01-create-subdomain-and-service.md) before reorganizing an application that still has API/Domain hosts.
