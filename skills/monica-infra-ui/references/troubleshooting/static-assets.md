# Diagnose Blazor startup scripts and static assets

Use this reference when `blazor.web.js` returns 404, a page keeps loading, CSS is empty, or a server-rendered page is visible but its controls do not respond. If the page URL itself fails only on direct access or refresh, use [routing and refresh](routing-and-refresh.md).

## Identify the failed request

Record the actual host process, .NET SDK/runtime, Monica version, environment name, and whether it runs build output or published output. Debug/Release are build configurations; Development/Production are runtime environments.

In browser Network and Console, inspect the script URL rendered by the document and the first failed startup request. The shared document uses `Assets["_framework/blazor.web.js"]`, so the deployed URL may be fingerprinted or include the application's base path. Inspect the requested URL, status, content type, and response body. A `200 OK` response containing HTML or zero bytes is not a successfully loaded script or stylesheet. Server prerendering can populate a page before any interactive circuit exists.

For a separate response check, replace the example origin and path with the observed resource URL:

```sh
curl -sS -D asset.headers -o asset.body -w 'status=%{http_code} bytes=%{size_download} type=%{content_type}\n' 'http://localhost:5000/_content/Monica.UI/css/mo-theme-main.css'
```

Use `curl.exe` on Windows PowerShell to select the native client. Inspect the saved body as well as the status; downloaded byte count remains useful when compression or transfer encoding makes `Content-Length` unavailable.

## Published host has no Blazor startup script

This branch applies to a .NET 10 Web executable whose Razor components all come from referenced libraries, including Monica's shared document. An executable with no `.razor` file needs `RequiresAspNetWebAssets=true` in its own project file so publish includes the Blazor startup script. Follow the project setup in [application hosting](../application-hosting.md); adding the property only to the presentation library does not configure the executable.

Check the published `_framework/blazor.web*.js` assets and `<HostAssembly>.staticwebassets.endpoints.json`. Republish after correcting the executable, deploy the matching output together, and verify the actual rendered script URL. If the script is present in that output but the request still fails, compare the serving process, document URL/base path, and proxy route with the deployed asset location before changing project properties again.

## Local build output runs with missing or empty CSS

A local IDE launch can bypass `launchSettings.json` and run `bin/Debug` output with the default Production environment. Missing development asset mappings may appear as 404s or empty responses, with a server message such as:

```text
The application is not running against the published output and Static Web Assets are not enabled.
```

Inspect `IHostEnvironment.EnvironmentName` or the startup log, the active IDE profile, `ASPNETCORE_ENVIRONMENT`, and `DOTNET_ENVIRONMENT`. Fix the intended local launch configuration and confirm it actually takes effect. Current `ModuleUIHost.ConfigureBuilder` calls `UseStaticWebAssets()` for a non-Production Web host, including Staging; it does not enable build-output mappings for Production. A production deployment should run its complete published output.

If the actual environment is already correct, check that UI hosting is composed, the referenced Razor libraries participate in the build, and the host's static-web-assets manifests contain the requested resources. The [hosting contract](../application-hosting.md) separates `AddUI()` foundation services from `AddUIHost()` document, route, and static-asset hosting. `UseMonica()` and `MapMonica()` complete the native host lifecycle.

## Assets load but controls remain inactive

Verify the Interactive Server connection and the first browser/server circuit error. Successful static requests alone do not prove interactivity. A custom document root must supply its script and interactive rendering as described in [application hosting](../application-hosting.md). Inspect connection or circuit evidence before treating this as another missing-file problem; use `$monica-infra-messaging` for a confirmed SignalR transport fault.

Finish by reloading the original URL, confirming nonempty JS/CSS responses and no startup failure, and performing a UI action that requires the interactive circuit. Verify the deployment form that failed; a successful local development run does not validate published output.

Source evidence: `Monica.UI/Modules/ModuleUIHost.cs`, `Monica.UI/Shell/Components/AppShell.razor`, and [ASP.NET Core's Blazor script inclusion rule](https://learn.microsoft.com/aspnet/core/blazor/project-structure?view=aspnetcore-10.0#location-of-the-blazor-script). [Static-asset URL resolution](https://learn.microsoft.com/aspnet/core/blazor/fundamentals/static-files?view=aspnetcore-10.0#deliver-assets-with-map-static-assets-routing-endpoint-conventions) explains fingerprinted resource URLs.
