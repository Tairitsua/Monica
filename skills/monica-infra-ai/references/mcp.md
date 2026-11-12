# MCP hosting and external tools

`AddMcp()` serves two independent needs: discover Monica-defined `McpServer<TSelf>` classes in the host's type-discovery scope, and catalog external HTTP MCP clients for agents. It brings in the AI and XML documentation modules. A discovered server exposes methods marked with Monica `SkillToolAttribute`; its `TransportKind` selects HTTP or stdio, and `IsLocalToolEnabled` separately controls whether the same tools are available directly to local chat agents. A server definition alone does not create an HTTP endpoint. For ordinary class and file skills, see [runtime skills](runtime-skills.md).

For a local HTTP server, use a Web host with `app.UseMonica()` and `app.MapMonica()`. The default endpoint pattern is `/mcp/{serverName}` and uses stateless Streamable HTTP. `ConfigureMcpHttpEndpoint(...)` changes the base path, displayed URL, or statefulness. The host owns its binding address and TLS. For a less trusted client boundary, register an ASP.NET Core authorization policy and use `RequireHttpAuthorization(policyName)`; authentication and authorization middleware must run before Monica maps the endpoint. `ModuleMcpOption.ExposeToolErrorDetail` defaults to `true` and includes exception type/message chains in unhandled tool errors, so turn it off when those details should stay inside the host.

Define a concrete server in an assembly included in Monica type discovery. Monica turns its `[SkillTool]` methods into MCP tools. This example intentionally selects HTTP; the server name becomes the final URL segment.

```csharp
using Monica.AI.Mcp.Abstractions;
using Monica.AI.Mcp.Models;
using Monica.Core.Skills.Annotations;

public sealed class OperationsMcpServer : McpServer<OperationsMcpServer>
{
    public override McpServerDefinition Definition { get; } =
        new("operations", "Read-only operational tools");

    public override McpServerTransportKind TransportKind => McpServerTransportKind.Http;

    [SkillTool(Name = "ping", Description = "Checks that the tool endpoint is reachable.")]
    public string Ping() => "pong";
}
```

```csharp
builder.AddMonica(monica =>
{
    monica.AddMcp(options => options.ExposeToolErrorDetail = false)
        .RequireHttpAuthorization("McpClients");
});
```

With the class above in the discovery scope, the registration maps `/mcp/operations`. Without a discovered enabled `McpServer<TSelf>` selecting HTTP, registering the module alone publishes no server. For stdio, the host uses its hosted-service transport instead of Web endpoint mapping. A skill-backed MCP server is controlled by the skill's `McpServerDefinition` and its separate persisted exposure setting. HTTP endpoints are materialized at startup, so the management result's restart requirement must remain visible after a toggle. Saving the preference does not verify that an endpoint is currently active. This preference is separate from in-process skill enablement and from exposing MCP tools to Monica agents.

With `Application:AutonomousWork=false`, the native stdio server remains admitted as an input transport, just as HTTP remains available. This does not start product background work: explicit MCP calls still execute their normal tool behavior and can have real external effects.

For a remote server, call `AddMcpClient(name, description, endpoint, ...)` on the MCP registration with an absolute HTTP(S) endpoint. That catalog entry can expose remote tools to agents without hosting a local MCP server.

```csharp
builder.AddMonica(monica =>
{
    monica.AddMcp()
        .AddMcpClient(
            "catalog",
            "Catalog search tools",
            builder.Configuration["Mcp:CatalogEndpoint"]
                ?? throw new InvalidOperationException("MCP endpoint is required."));
});
```

The packaged MCP catalog at `/ai/capabilities/mcp` links directly to Skills at `/ai/capabilities` and providers and models at `/ai-providers` as part of host-wide administration. It distinguishes local HTTP or stdio servers, skill-backed servers, code-defined remote clients, and user-managed external profiles. `AgentCapabilityFacade.GetManagementInfoAsync()` and its enablement methods expose the catalog/entry preferences and effective availability; see [runtime skills](runtime-skills.md) for the separate author, prerequisite, catalog, and entry gates. Enabling a catalog or entry controls agent access and does not establish endpoint connectivity or stop/start a server process.

`McpFacade.TestConnectivityAsync(name)` checks a registered entry. An external profile or local HTTP entry with an absolute display URL connects and lists tools. Local stdio and local HTTP without an absolute display URL report in-process readiness with an explanatory message; that success is not verification of external access. The result supplies the actual attempt time, duration, tool count, and tool names. An initial management query can discover uncached external tools, while a later UI refresh can use their cache; refresh is not a promise that every endpoint was retested.

`TestExternalProfileAsync(profile)` connects and lists tools using an unsaved, normalized external HTTP profile without persisting it; its transient client is disposed after the attempt. Bind result evidence to the complete captured profile, including endpoint, transport, timeout, description, exposure, and normalized headers. An edit makes the previous result stale rather than verifying the new draft. Normalize duplicate header names case-insensitively with the last value winning, matching `ExternalMcpClientProfile.Normalize`. A connectivity test is optional before save and cannot establish future availability.

`SaveExternalProfileAsync` and `DeleteExternalProfileAsync` manage user profiles and invalidate cached clients/tools. Successful deletion invalidates the external client and tool snapshot even when no entry-preference override existed, and its returned management view excludes the removed profile. They cannot overwrite code-defined or local entries. The packaged editor locks the profile name when editing, retains the full draft and diagnostic after a failed save, and closes only after authoritative save success. Closing or canceling retires its admitted tests and saves; cancellation cannot undo an already completed persistence operation. External profile state uses `ModuleMcpOption.ExternalMcpClientProfileStoreFilePath`, defaulting to `monica_data/ai/external_mcp_clients.json`. This option is independent of `ModuleAIOption.StorageRootPath`; the default capability state is `capabilities.json` under that AI root.

External profile headers are stored with the connection and can be included in its client configuration. Keep values in password inputs and out of ordinary summaries, and redact configured values from displayed diagnostics. The UI's deliberate Copy MCP configuration action includes configured external header values and discloses that fact; handle the resulting clipboard content according to the host's credential policy. These headers do not have the provider API-key write-only contract. Code-defined external profiles remain inspectable but cannot be changed through runtime profile management.

`McpFacade.ExportSkillPack(serverName, endpointUrl, targetRoot)` exports a local or skill-backed HTTP server as an Agent Skills pack. External clients and stdio servers cannot be exported through this operation. Supply an absolute HTTP(S) endpoint; the UI initially derives it from the configured display URL or the current host authority plus endpoint path. The pack contains instructions, tool references, and helper scripts that call that endpoint. The UI writes below an explicit directory on the server-process host filesystem, initially the host user's `.agents/skills`; it does not download an archive through the browser or register the exported skill. An existing target skill directory or file causes failure without overwrite. Write failures attempt to clean the partial directory and stay visible for retry; success reports the actual directory and tool count. Register or distribute the resulting pack deliberately, and enable script execution only for trusted roots as described in [runtime skills](runtime-skills.md).

If a hosted server appears absent, check concrete server discovery, author enablement, required modules, `TransportKind`, saved exposure/restart state, and endpoint mapping before debugging the client. If a remote tool is missing, inspect the profile's discovery error and effective gates, then run the explicit connectivity check. Source and checks: `Monica.AI/Modules/ModuleMcp.cs`, `Monica.AI/Mcp/Abstractions/McpServer.cs`, `Monica.AI/Mcp/Models/ExternalMcpClientProfile.cs`, `Monica.AI/Mcp/Facades/McpFacade.cs`, `Monica.AI/Mcp/Services/McpConnectivityService.cs`, `Monica.AI/Mcp/Services/McpSkillPackExporter.cs`, `Monica.AI/Facades/AgentCapabilityFacade.cs`, `tests/Test.Monica.AI/Modules/ModuleMcpTests.cs`, and `tests/Test.Monica.AI/Mcp/McpToolErrorDetailTests.cs`. Verify MCP SDK transport behavior against the exact package version when it matters.
