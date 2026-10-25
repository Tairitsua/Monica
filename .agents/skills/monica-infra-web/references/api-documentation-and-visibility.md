# API documentation and endpoint visibility

`AddSwagger()` may be registered directly; `AddWebApi()` already requires it. It adds Swagger generation and UI to a Web host. The primary business document defaults to `v1`; a separate Monica framework document is enabled by default. `AdditionalDocuments` adds more named documents. `DocumentNameResolver` can override an endpoint's document when it returns a registered name; otherwise a recognized `ApiDescription.GroupName` wins, then Monica-owned endpoints go to the Monica document, and remaining endpoints go to the business document. The UI route prefix defaults to `swagger`.

Direct registration requires the host's API Explorer services. Normal MVC/WebApi composition supplies controller discovery; register `builder.Services.AddEndpointsApiExplorer()` for Minimal API metadata, including in mixed hosts.

```csharp
monica.AddSwagger(options =>
{
    options.ApiVersion = "v1";
    options.BusinessDocumentTitle = "Ordering API";
});
```

`DocumentAssemblies` selects XML documentation sources when enabled; those projects need XML documentation output. `UseAuth` adds JWT bearer security information to generated OpenAPI documents. It does not by itself place an authorization policy on the Swagger UI route: set Web access policy separately when the UI must be restricted. If an endpoint exists but is missing from Swagger, inspect type discovery, its `ApiDescription` group, `DocumentNameResolver`, and the selected document before adding another controller.

The UI prefix changes the HTML location while generated documents remain in the `swagger/{documentName}/swagger.json` namespace. Monica's default document destinations resolve relative to the final UI location, including an empty or nested prefix and the current PathBase. The host's UI extension can replace those destinations and retains ownership of any changed or external URL. For shared themes, saved appearance and application toolbar links, register the separate Swagger workbench in `$monica-infra-ui` (`references/swagger-ui.md`); it preserves native generation and request execution.

Swagger-specific extension modules can register singleton `Monica.WebApi.Swagger.Abstractions.ISwaggerUIOptionsEnhancer` implementations. Swagger invokes `Apply` during middleware composition after the host's `ExtendSwaggerUIAction` and default URL resolution, in registration order. Keep request-specific work inside the resulting request factory; do not capture scoped circuit state or mutate shared options per request. An index decorator must preserve the host's final factory and native initializer and retain explicit ownership of every produced stream. Ordinary hosts continue to configure `ExtendSwaggerUIAction`; registering an enhancer is a module-extension contract.

Set routing and document overrides in `ExtendSwaggerUIAction`. An enhancer that changes the prefix afterward must also own matching document URLs; automatic URL resolution is not repeated after enhancement.

Monica modules can expose optional Minimal API diagnostic routes. The host's `ConfigureModuleSystem(options => options.EnableMinimalApiByDefault = true)` enables them by default, while a module deriving from `MinimalApiModuleOptions<T>` can override that decision; the host default is `false`. Absence of a diagnostic route does not mean its service is absent. Health probes use their own endpoint switches. `MonicaEndpointPort` restricts Monica-owned endpoints to one local port and can add a listener by default; deployments that own the binding can disable `AutoAddMonicaHttpListener`. These settings control whether and where a route is reachable, while [access-control.md](access-control.md) controls caller authentication, permission checks, and CORS.

Sources: `Monica.WebApi/Modules/ModuleSwagger.cs`, `Monica.WebApi/Swagger/Services/Support/SwaggerDocumentCatalog.cs`, `SwaggerGenOptionConfigurator.cs`, `Monica.Core/MonicaModuleSystemOptions.cs`, `Monica.Core/Modularity/Abstractions/MinimalApiModuleOptions.cs`, and `Monica.Core/Modularity/Extensions/MonicaApplicationBuilderExtensions.cs`.
