using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Monica.AI.Abstractions;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Extension methods for registering the optional AI HTTP endpoints module.
/// </summary>
public static class ModuleAIEndpointsBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers stateless HTTP endpoints for AI provider discovery.
        /// </summary>
        /// <param name="action">Optional endpoint configuration.</param>
        /// <returns>The host-bound AI endpoints module registration.</returns>
        public ModuleRegistration<ModuleAIEndpoints, ModuleAIEndpointsOption> AddAIEndpoints(Action<ModuleAIEndpointsOption>? action = null)
            => builder.AddModule<ModuleAIEndpoints, ModuleAIEndpointsOption>(action);
    }
}

/// <summary>
/// Hosts the optional stateless HTTP surface for Monica AI.
/// </summary>
public sealed class ModuleAIEndpoints : MonicaModule<ModuleAIEndpointsOption>, IWebHostRequiredModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleAI>();
    }

    /// <inheritdoc />
    public override void ValidateOptions(ModuleAIEndpointsOption options, string? profileName)
    {
        options.Normalize();
    }

    /// <inheritdoc />
    public override void ConfigureEndpoints(WebModuleContext<ModuleAIEndpointsOption> context)
    {
        var app = context.ApplicationBuilder;
        UseEndpoints(context, endpoints =>
        {
            endpoints.MapGet($"{Option.RoutePrefix}/providers", (IAIProviderFactory providers) =>
                    TypedResults.Ok(providers.GetAllProviderInfos()))
                .WithMonicaEndpoint();
        });
    }
}

/// <summary>
/// Configuration for Monica AI HTTP endpoints.
/// </summary>
public sealed class ModuleAIEndpointsOption : ModuleOptions<ModuleAIEndpoints>
{
    /// <summary>
    /// Gets or sets the route prefix used by all AI endpoints. The default is <c>/ai</c>.
    /// The value is normalized to one leading slash when module options are finalized.
    /// </summary>
    public string RoutePrefix { get; set; } = "/ai";

    internal void Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RoutePrefix);
        RoutePrefix = "/" + RoutePrefix.Trim().Trim('/');
    }
}

/// <summary>
/// Registration extensions for Monica AI HTTP endpoints.
/// </summary>
public static class ModuleAIEndpointsRegistrationExtensions
{
    /// <summary>
    /// Configures the common AI endpoint route prefix.
    /// </summary>
    /// <param name="module">The AI endpoints module registration to configure.</param>
    /// <param name="routePrefix">Route prefix such as <c>/ai</c>.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleAIEndpoints, ModuleAIEndpointsOption> MapAIEndpoints(this ModuleRegistration<ModuleAIEndpoints, ModuleAIEndpointsOption> module, string routePrefix = "/ai")
    {
        module.Configure(options => options.RoutePrefix = routePrefix);
        return module;
    }

}
