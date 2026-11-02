# Unified application debug mode

`Application:DebugMode` is the single host-owned diagnostic policy. It defaults to `false` in every environment and build configuration. Core resolves it before owned application registrations and exposes the frozen value through `IMonicaApplicationOptions.DebugMode`. No Configuration or UI module is required.

Supply the deployment input before `AddMonica`:

```json
{
  "Application": {
    "DebugMode": true
  }
}
```

The environment variable is `Application__DebugMode=true`. Without a deployment value, host code may supply a fallback inside the existing `builder.AddMonica(...)` callback:

```csharp
monica.ConfigureApplication(options => options.DebugMode = true);
```

Invalid boolean shapes and contradictions between explicit deployment and code values fail composition. Code/default provenance remains distinct from deployment input. The policy freezes before domain/role registrations, including through retained option references. Changing configuration later requires restart; reconnect browsers to establish new circuits. Development, `#if DEBUG`, an attached debugger, and `Application:AutonomousWork` never enable it implicitly.

## Capabilities and limits

Only already registered capabilities participate:

| Owner | Behavior when enabled |
| --- | --- |
| UI host | Detailed interactive circuit errors, Razor server-rendering errors, and Blazor component-hub errors. |
| SignalR | Detailed errors for business hubs. Registered business hubs validate the same policy. |
| Result envelope and exception handling | Bounded `metadata.diagnostics` exception catalogs and reserved response diagnostics; readable UTF-8 diagnostic JSON. |
| Invocation | Preserve reserved diagnostics actually supplied by downstream hosts. An upstream with debug disabled strips them; enabling an upstream does not enable a downstream. |
| Chain tracing | Attach existing call-chain and database-command evidence when the host has selected the response-attachment integration. |
| AutoModel | Existing expression-debug output through the configured logger, including original filters, generated expressions, and captured parameters. |

Details may contain stack traces, paths, exception messages, SQL and parameter values. Every client of the affected endpoints can receive them; this is not a per-user administrator preference. Existing bounds and redaction still apply. Diagnostic JSON is consumed as JSON, not inserted as raw HTML or script.

The UI host maps the native ASP.NET detailed-error options explicitly. Raw `DetailedErrors` configuration is not an alternate Monica switch. Final circuit/Razor options and mapped business-hub options that contradict the policy fail options validation at startup. Use the owning Monica registration rather than adding competing native option configurations. Other native hub settings remain configurable.

Debug mode does not register modules, tracing interceptors, exporters, endpoints, or background work. It does not install developer-exception middleware or recover a terminated circuit. Investigate the underlying exception in server logs. Server exception objects and public trace identifiers remain available with debug disabled; normal log filtering still applies.

## Independent policies and naming

| Setting | Responsibility |
| --- | --- |
| `OperationalPageAccess.BypassAuthorization` | Explicitly bypass operational-page authorization. Default false; the existing Development access rule remains independent. |
| `OptionDiagnosticsExposureMode` | Redact module-option values or explicitly reveal sensitive values. `RevealSensitive` still requires Development. |
| `ModuleRepositoryOption.EnableSensitiveDataLogging` | EF Core sensitive parameter logging under its existing repository contract. |
| Logging levels, body capture, profiling | Their own owners control volume and additional data collection. |
| `Application:AutonomousWork` | Automatic environmental work, independent of diagnostic disclosure. |

Do not use the word debug to describe an authorization bypass or sensitive-value reveal. The old `ModuleUIHostOption.EnableDebug`, `ModuleResultEnvelopeOption.ExposeDiagnosticDetails`, and `ModuleAutoModelOption.EnableDebugging` setters have been removed. Migrate those callers to `ConfigureApplication` or the canonical deployment input. Rename former `OperationalPageAccess.DebugMode` calls to `BypassAuthorization` only when preserving that explicit access decision.

## Inspect actual and candidate state

`MonicaApplication.GetDebugModeState()` reads the frozen actual value and source alongside the currently accepted deployment candidate. Invalid candidates report failure without replacing the running value. This is an explicit inspection read, not a runtime policy refresh.

When Configuration is installed, `ConfigurationFacade.GetDebugModeState()` exposes the same read-only evidence without accessing a store. The Configuration Diagnostics page displays both values, sources, and restart status. The Core scalar is not a separately materialized options definition or Managed document; overlapping Managed ownership is rejected. The module workbench also displays the frozen mode when Configuration is absent. These pages retain their ordinary access policies.

Source and behavior checks: `Monica.Core/MonicaApplicationOptions.cs`, `MonicaApplication.cs`, `Monica.UI/Modules/ModuleUIHost.cs`, `Monica.SignalR/Modules/ModuleSignalR.cs`, `tests/Test.Monica.Core/Modularity/ApplicationDebugModeTests.cs`, `tests/Test.Monica.Configuration/Bootstrap/ApplicationDebugModeProjectionTests.cs`, `tests/Test.Monica.UI/Hosting/ModuleUIHostDebugModeTests.cs`, and `tests/Test.Monica.SignalR/Modules/ModuleSignalRJsonProtocolTests.cs`.
