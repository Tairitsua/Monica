# API documentation and endpoint visibility

`AddSwagger()` may be registered directly; `AddWebApi()` already requires it. It adds Swagger generation and UI to a Web host. The primary business document defaults to `v1`; a separate Monica framework document is enabled by default. `AdditionalDocuments` adds more named documents. `DocumentNameResolver` can override an endpoint's document when it returns a registered name; otherwise a recognized `ApiDescription.GroupName` wins, then Monica-owned endpoints go to the Monica document, and remaining endpoints go to the business document. The UI route prefix defaults to `swagger`.

```csharp
monica.AddSwagger(options =>
{
    options.ApiVersion = "v1";
    options.BusinessDocumentTitle = "Ordering API";
});
```

`DocumentAssemblies` selects XML documentation sources when enabled; those projects need XML documentation output. `UseAuth` adds JWT bearer security information to generated OpenAPI documents. It does not by itself place an authorization policy on the Swagger UI route: set Web access policy separately when the UI must be restricted. If an endpoint exists but is missing from Swagger, inspect type discovery, its `ApiDescription` group, `DocumentNameResolver`, and the selected document before adding another controller.

Monica modules can expose optional Minimal API diagnostic routes. The host's `ConfigureModuleSystem(options => options.EnableMinimalApiByDefault = true)` enables them by default, while a module deriving from `MinimalApiModuleOptions<T>` can override that decision; the host default is `false`. Absence of a diagnostic route does not mean its service is absent. Health probes use their own endpoint switches. `MonicaEndpointPort` restricts Monica-owned endpoints to one local port and can add a listener by default; deployments that own the binding can disable `AutoAddMonicaHttpListener`. These settings control whether and where a route is reachable, while [access-control.md](access-control.md) controls caller authentication, permission checks, and CORS.

Sources: `Monica.WebApi/Modules/ModuleSwagger.cs`, `Monica.WebApi/Swagger/Services/Support/SwaggerDocumentCatalog.cs`, `SwaggerGenOptionConfigurator.cs`, `Monica.Core/MonicaModuleSystemOptions.cs`, `Monica.Core/Modularity/Abstractions/MinimalApiModuleOptions.cs`, and `Monica.Core/Modularity/Extensions/MonicaApplicationBuilderExtensions.cs`.
