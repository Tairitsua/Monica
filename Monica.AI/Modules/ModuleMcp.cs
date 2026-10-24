using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Monica.AI.Mcp.Abstractions;
using Monica.AI.Mcp.Facades;
using Monica.AI.Mcp.Internal;
using Monica.AI.Mcp.Models;
using Monica.AI.Mcp.Services;
using Monica.AI.AgentCapabilities.Abstractions;
using Monica.AI.Abstractions;
using Monica.AI.Services.Support.ModuleCatalog;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Core.Skills;
using Monica.Core.TypeDiscovery.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Extension methods for configuring Monica's MCP module.
/// </summary>
public static class ModuleMcpBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Enables Monica MCP server discovery, MCP hosting, and external MCP client cataloging.
        /// </summary>
        /// <param name="action">Optional MCP module configuration action.</param>
        /// <returns>The host-bound MCP module registration.</returns>
        public ModuleRegistration<ModuleMcp, ModuleMcpOption> AddMcp(Action<ModuleMcpOption>? action = null)
        {
            return builder.AddModule<ModuleMcp, ModuleMcpOption>(action);
        }
    }
}

/// <summary>
/// Discovers Monica-defined MCP servers and exposes them through configured MCP transports.
/// </summary>
public sealed class ModuleMcp : MonicaModule<ModuleMcpOption>, IWebModule
{
    private bool _hasDiscoveredMcpServers;
    private bool _hasDiscoveredSkills;

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleXmlDocumentation, ModuleXmlDocumentationOption>();
        module.Require<ModuleAI, ModuleAIOption>();
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleMcpOption> discovery)
    {
        discovery.Match(
            TypeQuery.ConcreteClass.AssignableTo<McpServer>(),
            (context, matches) =>
            {
                _hasDiscoveredMcpServers = matches.Count > 0;
                foreach (var match in matches)
                {
                    var mcpType = match.Type;
                    context.Registrations.TryAdd(ServiceDescriptor.Singleton(mcpType, mcpType));
                    context.Registrations.Add(
                        ServiceDescriptor.Singleton(
                            typeof(McpServer),
                            serviceProvider => (McpServer)serviceProvider.GetRequiredService(mcpType)));
                }
            });

        discovery.Match(
            TypeQuery.ConcreteClass.AssignableTo<Skill>(),
            (_, matches) =>
            {
                _hasDiscoveredSkills = matches.Count > 0;
            });
    }

    /// <inheritdoc />
    public override void PostConfigureServices(ModuleContext<ModuleMcpOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton<ILoadedModuleCatalog, ModuleRegistryLoadedModuleCatalog>();
        services.TryAddSingleton<MonicaMcpCatalog>();
        services.TryAddSingleton<McpSkillPackExporter>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentCapabilitySource, McpAgentCapabilitySource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAIChatAgentContributor, McpChatAgentContributor>());
        services.AddScoped(sp => new McpFacade(
            sp.GetRequiredService<IExternalMcpClientProfileStore>(),
            sp.GetRequiredService<MonicaMcpCatalog>(),
            sp.GetRequiredService<IAgentCapabilityService>(),
            sp.GetRequiredService<McpSkillPackExporter>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<ModelContextProtocol.Server.McpServerOptions>, McpServerOptionsConfigurator>());

        var mcpBuilder = services.AddMcpServer();
        if (_hasDiscoveredMcpServers || _hasDiscoveredSkills)
        {
            mcpBuilder.WithHttpTransport(transportOptions =>
            {
                transportOptions.Stateless = Option.McpHttpStateless;
                transportOptions.ConfigureSessionOptions = (httpContext, serverOptions, _) =>
                {
                    var catalog = httpContext.RequestServices.GetRequiredService<MonicaMcpCatalog>();
                    var serverName = httpContext.Request.RouteValues[ModuleMcpOption.HTTP_SERVER_ROUTE_VALUE]?.ToString();
                    if (!catalog.TryConfigureHttpServerOptions(serverName, serverOptions))
                    {
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(serverName)
                                ? "HTTP MCP server name is required in the endpoint route."
                                : $"HTTP MCP server '{serverName}' was not found or is not configured for HTTP transport.");
                    }

                    return Task.CompletedTask;
                };
            });
            services.AddHostedService<MonicaStdioMcpHostedService>();
        context.Composition.AdmitHostedLifecycle(
            services.Last(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)),
            typeof(MonicaStdioMcpHostedService));
        }

        services.TryAddSingleton<IExternalMcpClientProfileStore, FileExternalMcpClientProfileStore>();
        services.TryAddSingleton<ExternalMcpClientFactory>();

        foreach (var profile in Option.ExternalMcpClientProfiles)
        {
            services.AddSingleton(profile);
        }
    }

    /// <inheritdoc />
    public override void ConfigureEndpoints(WebModuleContext<ModuleMcpOption> context)
    {
        var app = context.ApplicationBuilder;
        var catalog = app.ApplicationServices.GetRequiredService<MonicaMcpCatalog>();
        if (!catalog.HasHttpServers)
        {
            return;
        }

        UseEndpoints(context, endpoints =>
        {
            var endpoint = endpoints.MapMcp(Option.CreateHttpEndpointRoutePattern())
                .WithMonicaEndpoint();

            if (!string.IsNullOrWhiteSpace(Option.McpHttpAuthorizationPolicy))
            {
                endpoint.RequireAuthorization(Option.McpHttpAuthorizationPolicy);
            }
        });
    }
}

/// <summary>
/// Configuration options for Monica's MCP module.
/// </summary>
public sealed class ModuleMcpOption : ModuleOptions<ModuleMcp>
{
    internal const string HTTP_SERVER_ROUTE_VALUE = "serverName";

    internal List<ExternalMcpClientProfile> ExternalMcpClientProfiles { get; } = [];

    /// <summary>
    /// Base route pattern used when Monica-defined MCP servers choose HTTP transport.
    /// Monica appends a <c>{serverName}</c> route segment so each logical HTTP MCP server owns an isolated tool namespace.
    /// The ASP.NET Core host remains responsible for binding addresses, ports, and TLS.
    /// </summary>
    public string McpHttpEndpointPath { get; set; } = "/mcp";

    /// <summary>
    /// Optional display URL shown in MCP management UIs when the externally reachable URL differs
    /// from the local route pattern.
    /// </summary>
    public string? McpHttpDisplayUrl { get; set; }

    /// <summary>
    /// Enables stateless Streamable HTTP transport for Monica's MCP endpoint. Stateless mode avoids
    /// server-side MCP session storage and maps only the HTTP POST endpoint.
    /// </summary>
    public bool McpHttpStateless { get; set; } = true;

    /// <summary>
    /// Creates the finalized local HTTP endpoint path for one logical MCP server from the current base route.
    /// </summary>
    /// <param name="serverName">Logical MCP server name appended as one escaped route segment.</param>
    /// <returns>The normalized endpoint path used by Monica's HTTP MCP endpoint.</returns>
    public string CreateHttpEndpointPath(string serverName)
    {
        return CreateHttpEndpointPath(McpHttpEndpointPath, serverName);
    }

    /// <summary>
    /// Optional ASP.NET Core authorization policy required by every Monica-hosted HTTP MCP endpoint.
    /// Leave this unset only for endpoints that are intentionally anonymous or protected by another host-level boundary.
    /// </summary>
    public string? McpHttpAuthorizationPolicy { get; set; }

    /// <summary>
    /// Exposes unhandled tool failure reasons to MCP clients: every exception escaping a tool is answered as a
    /// JSON-RPC internal error carrying the exception type and its message chain instead of the SDK's generic
    /// invocation error. Monica MCP surfaces are local-trust agent tooling, so this defaults to <c>true</c>;
    /// disable it when a server is exposed beyond a local trust boundary and clients must not see failure text.
    /// </summary>
    public bool ExposeToolErrorDetail { get; set; } = true;

    /// <summary>
    /// Relative or absolute file path used to persist runtime-managed external MCP client profiles.
    /// Defaults to <c>monica_data/ai/external_mcp_clients.json</c>.
    /// </summary>
    public string ExternalMcpClientProfileStoreFilePath { get; set; } = "monica_data/ai/external_mcp_clients.json";

    /// <summary>
    /// Adds a code-defined external MCP client profile to Monica's MCP catalog.
    /// </summary>
    /// <param name="profile">External MCP client profile to add.</param>
    public void AddMcpClient(ExternalMcpClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalized = profile.Normalize(ExternalMcpClientProfileOrigin.Code);
        normalized.Validate();
        ExternalMcpClientProfiles.Add(normalized);
    }

    internal string CreateHttpEndpointRoutePattern()
    {
        return AppendServerNameSegment(NormalizeHttpEndpointBasePath(McpHttpEndpointPath), rawServerName: false);
    }

    internal static string CreateHttpEndpointPath(string basePath, string serverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        return AppendServerNameSegment(NormalizeHttpEndpointBasePath(basePath), serverName);
    }

    internal static string? CreateHttpDisplayUrl(string? displayBaseUrl, string serverName)
    {
        if (string.IsNullOrWhiteSpace(displayBaseUrl))
        {
            return null;
        }

        var normalizedBaseUrl = displayBaseUrl.TrimEnd('/');
        return $"{normalizedBaseUrl}/{Uri.EscapeDataString(serverName.Trim())}";
    }

    private static string NormalizeHttpEndpointBasePath(string endpointPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointPath);

        var path = endpointPath.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.StartsWith('/') ? path : "/" + path;
    }

    private static string AppendServerNameSegment(string basePath, string? serverName = null, bool rawServerName = true)
    {
        var segment = rawServerName
            ? Uri.EscapeDataString(serverName ?? string.Empty)
            : $"{{{HTTP_SERVER_ROUTE_VALUE}}}";

        return string.IsNullOrWhiteSpace(basePath)
            ? "/" + segment
            : basePath + "/" + segment;
    }
}

/// <summary>
/// Registration extensions for Monica's MCP module.
/// </summary>
public static class ModuleMcpRegistrationExtensions
{
    /// <summary>
    /// Configures the HTTP route used by Monica-defined MCP servers that select HTTP transport.
    /// </summary>
    /// <param name="module">The MCP module registration.</param>
    /// <param name="endpointPath">ASP.NET Core base route pattern for HTTP MCP endpoints. Defaults to <c>"/mcp"</c>. Monica appends <c>/{serverName}</c>.</param>
    /// <param name="displayUrl">Optional externally reachable base URL shown in management UIs. Monica appends each MCP server name.</param>
    /// <param name="stateless">Whether Streamable HTTP should use stateless mode.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleMcp, ModuleMcpOption> ConfigureMcpHttpEndpoint(this ModuleRegistration<ModuleMcp, ModuleMcpOption> module,
        string endpointPath = "/mcp",
        string? displayUrl = null,
        bool stateless = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointPath);

        module.Configure(options =>
        {
            options.McpHttpEndpointPath = endpointPath;
            options.McpHttpDisplayUrl = displayUrl;
            options.McpHttpStateless = stateless;
        });
        return module;
    }

    /// <summary>
    /// Requires an ASP.NET Core authorization policy for every Monica-hosted HTTP MCP endpoint.
    /// </summary>
    /// <param name="module">The MCP module registration.</param>
    /// <param name="policyName">
    /// Name of a policy registered by the host through <c>AddAuthorization</c>. Authentication and authorization
    /// middleware must run before Monica maps its endpoints.
    /// </param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleMcp, ModuleMcpOption> RequireHttpAuthorization(this ModuleRegistration<ModuleMcp, ModuleMcpOption> module, string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        module.Configure(options => options.McpHttpAuthorizationPolicy = policyName.Trim());
        return module;
    }

    /// <summary>
    /// Registers an external HTTP MCP client whose tools may be exposed to Monica agents.
    /// </summary>
    /// <param name="module">The MCP module registration.</param>
    /// <param name="name">Stable client name shown to management UIs.</param>
    /// <param name="description">Short description of the remote MCP server or client connection.</param>
    /// <param name="endpoint">Absolute HTTP or HTTPS endpoint for the remote MCP server.</param>
    /// <param name="headers">Optional HTTP headers sent with MCP requests.</param>
    /// <param name="transportMode">HTTP transport mode used by the MCP SDK.</param>
    /// <param name="connectionTimeoutSeconds">Connection timeout in seconds.</param>
    /// <param name="isAgentToolEnabled">Whether listed client tools should be exposed to Monica agents.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleMcp, ModuleMcpOption> AddMcpClient(this ModuleRegistration<ModuleMcp, ModuleMcpOption> module,
        string name,
        string description,
        string endpoint,
        IDictionary<string, string>? headers = null,
        HttpTransportMode transportMode = HttpTransportMode.AutoDetect,
        int connectionTimeoutSeconds = 30,
        bool isAgentToolEnabled = true)
    {
        var profile = new ExternalMcpClientProfile
        {
            Name = name,
            Description = description,
            Endpoint = endpoint,
            Headers = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            TransportMode = transportMode,
            ConnectionTimeoutSeconds = connectionTimeoutSeconds,
            IsAgentToolEnabled = isAgentToolEnabled,
            Origin = ExternalMcpClientProfileOrigin.Code
        }.Normalize(ExternalMcpClientProfileOrigin.Code);

        module.Configure(options => options.AddMcpClient(profile));
        return module;
    }

}
