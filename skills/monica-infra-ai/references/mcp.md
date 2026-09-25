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

With the class above in the discovery scope, the registration maps `/mcp/operations`. Without a discovered enabled `McpServer<TSelf>` selecting HTTP, registering the module alone publishes no server. For stdio, the host uses its hosted-service transport instead of Web endpoint mapping. A skill-backed MCP server is controlled by the skill's `McpServerDefinition` and capability state; an exposure toggle may require restart because endpoints are materialized at startup.

For a remote server, call `AddMcpClient(name, description, endpoint, ...)` on the MCP registration with an absolute HTTP(S) endpoint. That catalog entry can expose remote tools to agents without hosting a local MCP server. `McpFacade.TestConnectivityAsync(name)` checks a registered entry; `TestExternalProfileAsync(profile)` tests a draft before saving it. `SaveExternalProfileAsync` and `DeleteExternalProfileAsync` manage user profiles but cannot overwrite code-defined or local entries. `AgentCapabilityFacade.GetManagementInfoAsync()` and its enablement methods show and control what agents may use. External profile state defaults to `monica_data/ai/external_mcp_clients.json`; capability state defaults to `monica_data/ai/capabilities_state.json`.

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

If a hosted server appears absent, check concrete server discovery, `IsEnabled`, required modules, `TransportKind`, and endpoint mapping before debugging the MCP client. If a remote tool is missing, check profile connectivity and its capability switch. Source and checks: `Monica.AI/Modules/ModuleMcp.cs`, `Monica.AI/Mcp/Abstractions/McpServer.cs`, `Monica.AI/Mcp/Facades/McpFacade.cs`, `Monica.AI/Facades/AgentCapabilityFacade.cs`, `tests/Test.Monica.AI/Modules/ModuleMcpTests.cs`, and `tests/Test.Monica.AI/Mcp/McpToolErrorDetailTests.cs`. Verify MCP SDK transport behavior against the exact package version when it matters.
