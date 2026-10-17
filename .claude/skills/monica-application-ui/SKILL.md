---
name: monica-application-ui
description: Design or review the presentation boundary of a Monica DDD application, including UI solution layout, project references, custom navigation, and local or remote application calls. Use for business application UI rather than framework diagnostic modules.
---

# Monica Application UI

Load `$monica-application` and the repository-designated application specification first. The same project specification owns backend and UI; do not create a competing UI architecture spec.

| Task | Read |
| --- | --- |
| Choose presentation projects, dependency direction, feature ownership, or deployment profile | [Presentation specification](references/presentation-specification.md) |
| Choose local invocation, remote publication, caller identity, or operation lifetime | [Application invocation](references/application-invocation.md) |
| Implement pages, interaction state, form models, or result handling | `$monica-application-ui-development` |
| Register foundation, routing, optional Shell, or application UI services | [UI hosting](../monica-infra-ui/references/application-hosting.md) |
| Verify application behavior and invocation boundaries | `$monica-application-unit-testing` |

Ordinary business pages require no Monica module or forwarding Facade. Keep business rules and transactions in backend use cases. Select the server-side profile unless the project explicitly adopts a browser-compatible contract and transport boundary.
