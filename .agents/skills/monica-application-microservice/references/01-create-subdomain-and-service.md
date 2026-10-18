# Migrate an existing service

The target is the [unified domain/role model](../../monica-application/references/03-unified-architecture.md). Do not create another `{Domain}Service.API` recipe for new work.

Map existing responsibilities before moving files:

| Existing owner | Target owner |
| --- | --- |
| Domain model, repositories, contexts and shared rules | `Domains/{Domain}/Domains.{Domain}` |
| API handlers and ordinary application behavior | `Domains/{Domain}/Roles.{Domain}` |
| Connector, Actor, realtime or other independently activated responsibility | A justified `Roles.{Domain}.{Purpose}` library |
| Product provider/bootstrap defaults | `Shared/Platform.Hosting` |
| Native composition and generated clients/local receivers | `AppHost/{Product}.Host` or a justified specialized host artifact |
| Actual domain migrations and context factories | `Migrations/Migrations.Domain{Domain}` |

Role libraries have no Program.cs. Keep role integrations inside their owning role rather than top-level Adapters. Reuse the existing domain identifier authority and preserve the strict published-language namespace. A specialized role does not create another business domain or database.

Preserve HTTP paths, AppIds, topics, hub paths, Actor identities, scheduler keys and durable subscriber identities, or record the explicit migration. Add assembly domain/role declarations and generate the shared ownership catalog where selective artifacts need it, and let each ApplicationHost record its actual local implementation inventory. Preserve stateful integration semantics: co-location does not authorize bypassing an Actor proxy or product primary-node policy.

Separate passive registration needed by ordinary local calls from selected active connections/subscriptions. Verify an included ordinary operation from an inactive role, an omitted published operation routed to its known remote owner, selected autonomous activation and required-role routing before switching production placement. Reconcile the project specification with the adopted graph and remaining migration gaps.
