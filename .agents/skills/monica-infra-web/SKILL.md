---
name: monica-infra-web
description: Compose Monica Web API, generated controllers, AutoModel, Swagger, authentication, authorization, CORS, and ProjectUnit HTTP integration. Use for host-facing API setup and route or execution-pipeline diagnosis.
---

# Monica Web integration

Choose host composition for the Web bundle or individual modules; choose endpoint guidance for request discovery and execution, and policy guidance for caller access.

| Task | Read |
| --- | --- |
| Compose an HTTP host and choose the Web bundle or individual capabilities | [Web composition](references/web-composition.md) |
| Expose request-owned endpoints, configure AutoModel, or inspect and extend execution behaviors | [Endpoints and execution](references/endpoints-and-execution.md) |
| Configure JWT authentication, permission checks, or CORS | [Access control](references/access-control.md) |
| Configure Swagger documents or control the visibility of module routes | [API documentation and visibility](references/api-documentation-and-visibility.md) |

For application request and handler design, use ProjectUnit development in `$monica-application-project-unit-development`. For module implementation, use `$monica-framework`, then `$monica-development`.

If a named skill is unavailable, use `$monica-guide` to resolve the verified Monica checkout (`source resolve --repository monica --json`) and read its canonical `skills/<name>/SKILL.md` and specified reference.
