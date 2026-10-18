# Monica public documentation writing rules

Write for a developer adopting or configuring Monica. Begin with the capability and a correct, usable example; explain required setup, defaults, and tradeoffs where they affect decisions. Use the site's current frontmatter and navigation conventions rather than imposing one page template on every topic.

Current source code owns framework facts. Existing Monica.Docs pages, README files, samples, and tests help locate examples, but verify API names and behavior before reuse. Document public builder extensions, options, facades, abstractions, request contracts, and models. Mention internal services only to explain behavior exposed by that public surface.

In samples, register modules inside `builder.AddMonica(monica => { ... })`. For a complete web host, map the lifecycle with `app.UseMonica()` and `app.MapMonica()`. Do not publish removed ambient `Mo.Add*()`, `builder.UseMonica()`, or `Mo.RegisterInstantly(...)` calls. On generated endpoints, put `[ApiEndpoint]` and its route/binding metadata on the request, not MVC attributes on the `ApplicationService`. Distinguish local HTTP from API publication: the attributed request must be in the exact PublishedLanguages.Domain{DomainName}.Requests namespace. Interfaces are independent of HTTP publication; hosts choose adapters and owned receivers.

Use a table when several options or provider choices need comparison. Read defaults from property initializers and map `RequireFeature(...)` requirements to the actual public registration methods that satisfy them. Omit empty or speculative sections.

Use relative links for local pages and assets, including site-local attachments. Keep code identifiers in English; write natural English in `en-US` and natural Simplified Chinese in `zh-CN`. When both locales are in scope, keep examples, defaults, public claims, and navigation aligned.
