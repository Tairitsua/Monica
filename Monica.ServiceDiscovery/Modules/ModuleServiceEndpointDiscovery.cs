using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.ServiceDiscovery;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.ServiceDiscovery.Facades;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Metrics;
using Monica.ServiceDiscovery.Services;

namespace Monica.Modules;

/// <summary>
/// Composes native Microsoft endpoint resolution and bounded process diagnostics.
/// Independent from registry membership, heartbeats, state stores and leader election.
/// </summary>
public sealed class ModuleServiceEndpointDiscovery : MonicaModule<ModuleServiceEndpointDiscoveryOption>
{
    /// <inheritdoc />
    public override void ValidateOptions(ModuleServiceEndpointDiscoveryOption options, string? profileName)
    {
        if (options.Targets.Count > 256 || options.MaxCallsPerTarget is < 1 or > 1000 ||
            options.Retention <= TimeSpan.Zero || options.Retention > TimeSpan.FromDays(1) ||
            options.ResolutionTimeout <= TimeSpan.Zero || options.ResolutionTimeout > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("Invalid endpoint diagnostic limits.");
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in options.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.Scope) || string.IsNullOrWhiteSpace(target.AppId) ||
                target.Scope.Length > 128 || target.AppId.Length > 128 ||
                !target.Address.IsAbsoluteUri || target.Address.Scheme is not ("http" or "https" or "https+http") ||
                target.Address.AbsolutePath != "/" || target.Address.UserInfo.Length != 0 ||
                target.Address.Query.Length != 0 || target.Address.Fragment.Length != 0 ||
                !addresses.Add(target.Address.AbsoluteUri))
                throw new InvalidOperationException("Endpoint targets require unique, credential-free logical HTTP addresses and bounded identities.");
        }
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleServiceEndpointDiscoveryOption> context)
    {
        context.Services.AddServiceDiscoveryCore().AddConfigurationServiceEndpointProvider();
        if (Option.EnablePassThrough)
            context.Services.AddPassThroughServiceEndpointProvider();
        context.Services.AddMetrics();
        context.Services.AddSingleton<EndpointDiagnostics>();
        context.Services.AddSingleton(sp => new ServiceEndpointDiscoveryFacade(
            sp.GetRequiredService<ServiceEndpointResolver>(), sp.GetRequiredService<EndpointDiagnostics>(),
            sp.GetRequiredService<IOptions<ModuleServiceEndpointDiscoveryOption>>()));
    }
}

/// <summary>Configuration of discovery integration and diagnostic retention, fixed at host startup.</summary>
public sealed class ModuleServiceEndpointDiscoveryOption : ModuleOptions<ModuleServiceEndpointDiscovery>
{
    /// <summary>Known destinations allowed for UI resolution and HTTP diagnostics. Maximum 256; empty by default.</summary>
    public List<ServiceEndpointTarget> Targets { get; set; } = [];
    /// <summary>
    /// Enables Microsoft's DNS pass-through provider after configuration. Default false: absent configuration
    /// fails resolution. Pass-through describes a DNS destination; it does not query DNS or prove readiness.
    /// </summary>
    public bool EnablePassThrough { get; set; }
    /// <summary>Maximum retained HTTP observations per target. Default 200; valid range 1–1000.</summary>
    public int MaxCallsPerTarget { get; set; } = 200;
    /// <summary>Maximum observation age. Default 15 minutes; maximum one day.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>Deadline for each explicit diagnostic resolution. Default 5 seconds; maximum one minute.</summary>
    public TimeSpan ResolutionTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Host composition and HTTP integration for endpoint discovery.</summary>
public static class ModuleServiceEndpointDiscoveryBuilderExtensions
{
    /// <summary>Enables endpoint discovery without enabling registry membership or UI.</summary>
    public static ModuleRegistration<ModuleServiceEndpointDiscovery, ModuleServiceEndpointDiscoveryOption>
        AddServiceEndpointDiscovery(this IMonicaBuilder builder, Action<ModuleServiceEndpointDiscoveryOption>? configure = null) =>
        builder.AddModule<ModuleServiceEndpointDiscovery, ModuleServiceEndpointDiscoveryOption>(configure);

    /// <summary>
    /// Adds native discovery with bounded diagnostics. Requires AddServiceEndpointDiscovery on the same host.
    /// Call once per client, before any inner handlers. Unknown targets still resolve but are not retained.
    /// Primary handlers should disable redirects when the selected endpoint must represent the actual destination.
    /// </summary>
    public static IHttpClientBuilder AddMonicaServiceDiscovery(this IHttpClientBuilder builder) =>
        builder.AddHttpMessageHandler(sp => new EndpointObservationHandler(sp.GetRequiredService<EndpointDiagnostics>()))
            .AddServiceDiscovery()
            .AddHttpMessageHandler(() => new SelectedEndpointHandler());
}
