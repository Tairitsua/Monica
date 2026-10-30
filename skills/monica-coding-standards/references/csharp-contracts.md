# C# coding contracts

These conventions apply to Monica source. Consumer and sibling repositories own their own coding rules.

## Developer-facing documentation

Write code comments, XML documentation, and developer-facing annotations in English. Document public abstractions, models, attributes, module options, and registration/builder extensions at the declaration that owns the contract.

- Options explain purpose, effect, significant defaults, and when to configure them.
- Registration and builder extensions explain what they enable, prerequisites, and notable side effects or constraints.
- Abstractions explain lifecycle and ownership, nullability, and exception, cancellation, or timeout behavior when relevant.
- Internal comments explain non-obvious branching, concurrency, normalization, caching, or coordination; omit narration of obvious code.

## C# conventions

Use upper snake case for private constants, such as `DEFAULT_SEARCH_TOOL_NAME`. Use a primary constructor for a dependency-injected class with one constructor. Consume `Module{Name}Option` through `IOptions<T>` or `IOptionsSnapshot<T>` according to the consumer's lifetime; do not add another options accessor solely to retrieve module configuration.

Result-envelope and layer boundaries remain owned by `$monica-development` and `$monica-architecture`. Apply the [coding standards](../SKILL.md) when deciding whether a check, fallback, or abstraction belongs in the implementation.
