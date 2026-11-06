using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Configuration.Abstractions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.Modularity.Services;
using Monica.Core.TypeDiscovery.Models;
using Monica.Core.XmlDocumentation.Abstractions;
using Monica.BuildingBlocks.Abstractions;
using Monica.BuildingBlocks.Facades;
using Monica.BuildingBlocks.Models;
using Monica.BuildingBlocks.Providers.AspNetCore;
using Monica.BuildingBlocks.Services;
using Monica.BuildingBlocks.Services.Support;
using Monica.Tool.Extensions;
using Monica.WebApi.AutoControllers.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleBuildingBlocksBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers host-scoped project architecture discovery, diagnostics, and optional request filtering.
        /// </summary>
        /// <param name="action">An optional callback that configures discovery and endpoint behavior.</param>
        /// <returns>The host-bound module registration.</returns>
        public ModuleRegistration<ModuleBuildingBlocks, ModuleBuildingBlocksOption> AddBuildingBlocks(Action<ModuleBuildingBlocksOption>? action = null)
        {
            return builder.AddModule<ModuleBuildingBlocks, ModuleBuildingBlocksOption>(action);
        }
    }
}

/// <summary>
/// Discovers application architecture building blocks for the current host and exposes optional inspection endpoints.
/// </summary>
public class ModuleBuildingBlocks : MonicaModule<ModuleBuildingBlocksOption>, IWebModule
{
    private readonly BuildingBlockDiscoveryPlan _discoveryPlan = new();
    private BuildingBlockCatalog? _catalog;

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleJsonSerialization, ModuleJsonSerializationOption>();
        module.Require<ModuleEventBus, ModuleEventBusOption>();
        module.AfterIfPresent<ModuleAutoControllers, ModuleAutoControllersOption>();
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleBuildingBlocksOption> context)
    {
        var services = context.Services;

        services.AddSingleton(ResolveCatalog);
        services.AddSingleton<IBuildingBlockCatalog>(serviceProvider =>
            serviceProvider.GetRequiredService<BuildingBlockCatalog>());
        services.TryAddScoped<IBuildingBlockRequirementLinkResolver, NullBuildingBlockRequirementLinkResolver>();
        services.AddScoped<BuildingBlockProjectionService>();
        services.AddScoped<BuildingBlockCatalogService>();
        services.AddScoped<BuildingBlocksFacade>();

        if (Option.EnableRequestFilter)
        {
            services.AddRequestFilter();
        }
    }

    /// <inheritdoc />
    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleBuildingBlocksOption> discovery)
    {
        discovery.Match(
            TypeQuery.All,
            (_, matches) => _discoveryPlan.Publish(matches.Select(static match => match.Shape)));
    }

    /// <inheritdoc />
    public override void PostConfigureServices(ModuleContext<ModuleBuildingBlocksOption> context)
    {
        // Structural analysis must complete during composition: Monica.Configuration publishes
        // definition metadata while the host pipeline is built, so options-usage inference enriches
        // the definition registry before any publication snapshot can observe the scanner's
        // unresolved defaults.
        _catalog = BuildingBlockCatalog.CreateAnalyzed(
            Option,
            CreateEffectiveNamingOptions(context.Modules),
            Logger,
            _discoveryPlan.GetRequiredSnapshot(),
            ResolveDefinitionRegistry(context.Services));
    }

    private BuildingBlockCatalog ResolveCatalog(IServiceProvider serviceProvider)
    {
        var catalog = _catalog
            ?? throw new InvalidOperationException(
                $"{nameof(ModuleBuildingBlocks)} structural analysis has not completed for this host.");
        catalog.AttachDocumentation(Option.IncludeDocumentation
            ? serviceProvider.GetService<IXmlDocumentationService>()
            : null);
        return catalog;
    }

    // ModuleConfiguration exposes its definition registry as an instance singleton, so composition-time
    // inference can pull the live instance from the finalized service collection without a provider.
    private static IConfigurationDefinitionRegistry? ResolveDefinitionRegistry(IServiceCollection services)
    {
        return services
            .LastOrDefault(descriptor =>
                !descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(IConfigurationDefinitionRegistry))
            ?.ImplementationInstance as IConfigurationDefinitionRegistry;
    }

    private sealed class RequestFilterDto
    {
        public List<string>? Urls { get; set; }
        public bool? Disable { get; set; }
    }

    /// <inheritdoc />
    public override void ConfigureApplicationBuilder(WebModuleContext<ModuleBuildingBlocksOption> context)
    {
        var app = context.ApplicationBuilder;
        if (Option.EnableRequestFilter)
        {
            app.UseRequestFilter();
        }
    }

    /// <inheritdoc />
    public override void ConfigureEndpoints(WebModuleContext<ModuleBuildingBlocksOption> context)
    {
        var app = context.ApplicationBuilder;
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapPost("/framework/building-blocks/domain-event/{eventKey}/publish",
                async ([FromRoute] string eventKey,
                      [FromServices] BuildingBlocksFacade buildingBlocksFacade,
                      [FromBody] JsonNode eventContent) =>
                {
                    return await buildingBlocksFacade.PublishDomainEventAsync(eventKey, eventContent);
                })
                .WithName("BuildingBlocks.PublishDomainEvent")
                .WithTags(tagName)
                .WithSummary("Publish a discovered domain event")
                .WithDescription("Deserializes and publishes a domain event by its discovered building-block key.");

            if (Option.EnableRequestFilter)
            {
                endpoints.MapPost("/framework/request-filter",
                    async ([FromBody] RequestFilterDto dto,
                          [FromServices] BuildingBlocksFacade buildingBlocksFacade) =>
                    {
                        return await buildingBlocksFacade.ManageRequestFilterAsync(dto.Urls, dto.Disable);
                    })
                    .WithName("BuildingBlocks.ConfigureRequestFilter")
                    .WithTags(tagName)
                    .WithSummary("Configure the building-block request filter")
                    .WithDescription("Enables or disables host-local request paths when request filtering is configured.");
            }

            endpoints.MapGet("/framework/building-blocks",
                async ([FromServices] BuildingBlocksFacade buildingBlocksFacade) =>
                {
                    return await buildingBlocksFacade.GetAllAsync();
                })
                .WithName("BuildingBlocks.List")
                .WithTags(tagName)
                .WithSummary("List discovered building blocks")
                .WithDescription("Returns every building block discovered for the current host.");

            endpoints.MapGet("/framework/building-blocks/dashboard",
                async ([FromServices] BuildingBlocksFacade buildingBlocksFacade) =>
                {
                    return await buildingBlocksFacade.GetDashboardAsync();
                })
                .WithName("BuildingBlocks.Dashboard")
                .WithTags(tagName)
                .WithSummary("Get the building-block status dashboard")
                .WithDescription("Returns host identity, architecture health, independent context coverage, and actionable gaps.");

            endpoints.MapGet("/framework/building-blocks/domain-event",
                async ([FromServices] BuildingBlocksFacade buildingBlocksFacade) =>
                {
                    return await buildingBlocksFacade.GetDomainEventsAsync();
                })
                .WithName("BuildingBlocks.ListDomainEvents")
                .WithTags(tagName)
                .WithSummary("List discovered domain events")
                .WithDescription("Returns domain-event building blocks and their serializable structure.");

            endpoints.MapGet("/framework/building-blocks/{key}",
                async ([FromRoute] string key,
                      [FromServices] BuildingBlocksFacade buildingBlocksFacade,
                      CancellationToken cancellationToken) =>
                {
                    return await buildingBlocksFacade.GetDetailAsync(key, cancellationToken);
                })
                .WithName("BuildingBlocks.Detail")
                .WithTags(tagName)
                .WithSummary("Get building-block detail")
                .WithDescription("Returns typed building-block metadata, dependencies, methods, and requirement links.");

            endpoints.MapGet("/framework/enum",
                async ([FromServices] BuildingBlocksFacade buildingBlocksFacade,
                      [FromQuery] string? name = null) =>
                {
                    return await buildingBlocksFacade.GetEnumInfoAsync(name);
                })
                .WithName("BuildingBlocks.ListEnums")
                .WithTags(tagName)
                .WithSummary("List discovered enum metadata")
                .WithDescription("Returns discovered enum names and values, optionally filtered by enum name.");
        });
    }

    /// <summary>
    /// Uses the optional AutoControllers edge as the single source of truth for the derived CRUD service suffix.
    /// An explicit Building Blocks convention remains authoritative.
    /// </summary>
    private BuildingBlockNamingOptions CreateEffectiveNamingOptions(IModuleOptionReader modules)
    {
        var configured = Option.Naming;
        var effective = new BuildingBlockNamingOptions
        {
            Rules = new Dictionary<BuildingBlockKind, BuildingBlockNamingRule>(configured.Rules),
            Enabled = configured.Enabled,
            Mode = configured.Mode
        };

        if (!effective.Rules.ContainsKey(BuildingBlockKind.CrudApplicationService)
            && modules.TryGet<ModuleAutoControllers, ModuleAutoControllersOption>(out var autoControllersOptions)
            && autoControllersOptions is { } configuredAutoControllers
            && !string.IsNullOrEmpty(configuredAutoControllers.Crud.CrudControllerPostfix))
        {
            effective.Rules[BuildingBlockKind.CrudApplicationService] = new BuildingBlockNamingRule
            {
                Suffix = configuredAutoControllers.Crud.CrudControllerPostfix
            };
        }

        return effective;
    }
}

/// <summary>
/// Provides fluent registration for the Building Blocks module.
/// </summary>
public static class ModuleBuildingBlocksRegistrationExtensions
{
    /// <summary>
    /// Enables XML documentation analysis for discovered building blocks.
    /// </summary>
    /// <param name="module">The Building Blocks module registration.</param>
    public static ModuleRegistration<ModuleBuildingBlocks, ModuleBuildingBlocksOption> WithDocumentationDetails(
        this ModuleRegistration<ModuleBuildingBlocks, ModuleBuildingBlocksOption> module)
    {
        module.Configure(options => options.IncludeDocumentation = true);
        module.Require<ModuleXmlDocumentation, ModuleXmlDocumentationOption>();
        return module;
    }

    /// <summary>
    /// Registers the application-owned resolver used to turn requirement identifiers into optional navigation links.
    /// </summary>
    /// <param name="module">The Building Blocks module registration.</param>
    /// <typeparam name="TResolver">A scoped requirement-link resolver implementation.</typeparam>
    /// <returns>The current module registration.</returns>
    /// <remarks>
    /// Resolution occurs only when building-block detail is requested. Unknown requirements should return
    /// <see langword="null"/> so they remain visible as unresolved references.
    /// </remarks>
    public static ModuleRegistration<ModuleBuildingBlocks, ModuleBuildingBlocksOption> UseRequirementLinkResolver<TResolver>(this ModuleRegistration<ModuleBuildingBlocks, ModuleBuildingBlocksOption> module)
        where TResolver : class, IBuildingBlockRequirementLinkResolver
    {
        module.ConfigureServices(context =>
        {
            context.Services.Replace(
                ServiceDescriptor.Scoped<IBuildingBlockRequirementLinkResolver, TResolver>());
        });
        return module;
    }

}

/// <summary>
/// Configures host-scoped building-block discovery and diagnostic endpoints.
/// </summary>
public class ModuleBuildingBlocksOption : MinimalApiModuleOptions<ModuleBuildingBlocks>
{
    /// <summary>
    /// Gets or sets the naming rules used to validate discovered architectural building blocks.
    /// </summary>
    public BuildingBlockNamingOptions Naming { get; set; } = new();

    /// <summary>
    /// Gets or sets whether the request-filter middleware and its management endpoint are enabled.
    /// The filter is disabled by default and its path state is isolated to the current host.
    /// </summary>
    public bool EnableRequestFilter { get; set; }

    /// <summary>
    /// Gets or sets whether Building Blocks loads XML summaries for discovered types and methods.
    /// The default is <see langword="false"/>. Enable it through
    /// <see cref="ModuleBuildingBlocksRegistrationExtensions.WithDocumentationDetails"/> so the required XML
    /// documentation module is declared explicitly with the feature.
    /// </summary>
    public bool IncludeDocumentation { get; internal set; }
}

/// <summary>
/// Configures building-block naming validation globally and per architectural category.
/// </summary>
public class BuildingBlockNamingOptions
{
    /// <summary>
    /// Gets or sets category-specific naming rules. Missing categories use their building-block model defaults.
    /// </summary>
    public Dictionary<BuildingBlockKind, BuildingBlockNamingRule> Rules { get; set; } = [];

    /// <summary>
    /// Gets or sets the fallback behavior when a naming rule is violated. The default records a warning.
    /// </summary>
    public BuildingBlockNamingMode Mode { get; set; } = BuildingBlockNamingMode.Warning;

    /// <summary>
    /// Gets or sets whether naming conventions are validated. Validation is disabled by default.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Describes the required fragments of a building-block CLR type name and namespace.
/// </summary>
public class BuildingBlockNamingRule
{
    /// <summary>
    /// Gets or sets the required type-name suffix.
    /// </summary>
    public string? Suffix { get; set; }

    /// <summary>
    /// Gets or sets the required type-name prefix.
    /// </summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Gets or sets a type-name fragment recorded for diagnostics.
    /// </summary>
    public string? Contains { get; set; }

    /// <summary>
    /// Gets or sets a namespace fragment recorded for diagnostics.
    /// </summary>
    public string? NamespaceContains { get; set; }

    /// <summary>
    /// Gets or sets the violation behavior for this rule. The default is <see langword="null"/>, which uses the global behavior.
    /// </summary>
    public BuildingBlockNamingMode? Mode { get; set; }

    public override string ToString()
    {
        return $"{Suffix?.Be("Suffix: {0}\n", true)}{Prefix?.Be("Prefix: {0}\n", true)}{Contains?.Be("Contains: {0}\n", true)}{NamespaceContains?.Be("Namespace contains: {0}", true)}".TrimEnd();
    }
}

/// <summary>
/// Specifies how Building Blocks responds to naming-convention violations.
/// </summary>
public enum BuildingBlockNamingMode
{
    /// <summary>
    /// Records an alert and allows host startup to continue.
    /// </summary>
    Warning,

    /// <summary>
    /// Records an error and stops host startup.
    /// </summary>
    Strict,

    /// <summary>
    /// Ignores naming-convention violations for the rule.
    /// </summary>
    Disable
}
