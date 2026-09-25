# Logging and local inspection

Add `monica.AddLogging()` inside the host's `AddMonica(...)` callback to install Monica's Serilog provider during builder composition. It clears existing logging providers, so decide at composition time whether this host should use it. Console and file sinks are enabled by default; set `EnableFileSink = false` for a console-only deployment. `LogFilePath` takes precedence over `LogDirectory` and `LogFileName`, and `CustomLoggerFactory` replaces the default Serilog setup. Keep endpoint, sink, and credential settings in host configuration.

```csharp
monica.AddLogging(options =>
{
    options.EnableFileSink = false;
    options.EnableTraceIdEnricher = true;
});
```

`AddRequestResponseLoggingMiddleware()` is an opt-in Web feature on the logging registration. It runs before routing and can log request and response bodies; decide whether those bodies should be captured before enabling it. Its `disableRequest` and `disableResponse` parameters allow one side to be excluded. Operator exception logs receive the full exception independently of [response diagnostic visibility](../../monica-infra-hosting/references/exception-diagnostics.md).

For a Web host that needs interactive local file inspection, `monica.AddLoggingUI()` adds a shell page and requires Logging, ResultEnvelope, localization, and the shell. Its file-query service lists and opens files beneath the resolved log directory; its tail service follows a selected file, and the screen buffer can export current lines. Set `ModuleLoggingUIOption.LogDirectory` if the UI should inspect a different directory from the logging sink. The UI defaults to 500 initial lines, 5,000 retained display lines, one-second polling, and capture-only matches. The optional `/logging-ui/files` list route follows the Minimal API switch, while the UI's file download and buffer-export routes remain mapped when the UI module is installed. Choose the UI's host access policy through `$monica-infra-ui`; file inspection is not an external log aggregation service.

If the page is empty, first check whether the file sink is enabled, whether its path matches the UI query directory, and whether the file exists. For a console-only host, inspect the console or external collector rather than enabling the UI to search a nonexistent file. Sources: `Monica.Logging/Modules/ModuleLogging.cs`, `Monica.Framework.UI/Modules/ModuleLoggingUI.cs`, `Monica.Framework.UI/UILogging/Support/LogFileQueryService.cs`, and `LogTailService.cs`.
