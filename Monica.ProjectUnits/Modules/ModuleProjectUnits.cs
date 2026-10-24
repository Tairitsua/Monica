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
using Monica.ProjectUnits.Abstractions;
using Monica.ProjectUnits.Facades;
using Monica.ProjectUnits.Models;
using Monica.ProjectUnits.Providers.AspNetCore;
using Monica.ProjectUnits.Services;
using Monica.ProjectUnits.Services.Support;
using Monica.Tool.Extensions;
using Monica.WebApi.AutoControllers.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleProjectUnitsBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers host-scoped project architecture discovery, diagnostics, and optional request filtering.
        /// </summary>
        /// <param name="action">An optional callback that configures discovery and endpoint behavior.</param>
        /// <returns>The host-bound module registration.</returns>
        public ModuleRegistration<ModuleProjectUnits, ModuleProjectUnitsOption> AddProjectUnits(Action<ModuleProjectUnitsOption>? action = null)
        {
            return builder.AddModule<ModuleProjectUnits, ModuleProjectUnitsOption>(action);
        }
    }
}

/// <summary>
/// Discovers application architecture units for the current host and exposes optional inspection endpoints.
/// </summary>
public class ModuleProjectUnits : MonicaModule<ModuleProjectUnitsOption>, IWebModule
{
    private readonly ProjectUnitDiscoveryPlan _discoveryPlan = new();
    private ProjectUnitCatalog? _catalog;

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleJsonSerialization, ModuleJsonSerializationOption>();
        module.Require<ModuleEventBus, ModuleEventBusOption>();
        module.AfterIfPresent<ModuleAutoControllers, ModuleAutoControllersOption>();
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleProjectUnitsOption> context)
    {
        var services = context.Services;

        services.AddSingleton(ResolveCatalog);
        services.AddSingleton<IProjectUnitCatalog>(serviceProvider =>
            serviceProvider.GetRequiredService<ProjectUnitCatalog>());
        services.TryAddScoped<IProjectUnitRequirementLinkResolver, NullProjectUnitRequirementLinkResolver>();
        services.AddScoped<ProjectUnitProjectionService>();
        services.AddScoped<ProjectUnitCatalogService>();
        services.AddScoped<ProjectUnitsFacade>();

        if (Option.EnableRequestFilter)
        {
            services.AddRequestFilter();
        }
    }

    /// <inheritdoc />
    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleProjectUnitsOption> discovery)
    {
        discovery.Match(
            TypeQuery.All,
            (_, matches) => _discoveryPlan.Publish(matches.Select(static match => match.Shape)));
    }

    /// <inheritdoc />
    public override void PostConfigureServices(ModuleContext<ModuleProjectUnitsOption> context)
    {
        // Structural analysis must complete during composition: Monica.Configuration publishes
        // definition metadata while the host pipeline is built, so options-usage inference enriches
        // the definition registry before any publication snapshot can observe the scanner's
        // unresolved defaults.
        _catalog = ProjectUnitCatalog.CreateAnalyzed(
            Option,
            CreateEffectiveNamingOptions(context.Modules),
            Logger,
            _discoveryPlan.GetRequiredSnapshot(),
            ResolveDefinitionRegistry(context.Services));
    }

    private ProjectUnitCatalog ResolveCatalog(IServiceProvider serviceProvider)
    {
        var catalog = _catalog
            ?? throw new InvalidOperationException(
                $"{nameof(ModuleProjectUnits)} structural analysis has not completed for this host.");
        catalog.AttachDocumentation(Option.ParseUnitDetails
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
    public override void ConfigureApplicationBuilder(WebModuleContext<ModuleProjectUnitsOption> context)
    {
        var app = context.ApplicationBuilder;
        if (Option.EnableRequestFilter)
        {
            app.UseRequestFilter();
        }
    }

    /// <inheritdoc />
    public override void ConfigureEndpoints(WebModuleContext<ModuleProjectUnitsOption> context)
    {
        var app = context.ApplicationBuilder;
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapPost("/framework/units/domain-event/{eventKey}/publish",
                async ([FromRoute] string eventKey,
                      [FromServices] ProjectUnitsFacade projectUnitsFacade,
                      [FromBody] JsonNode eventContent) =>
                {
                    return await projectUnitsFacade.PublishDomainEventAsync(eventKey, eventContent);
                })
                .WithName("ProjectUnits.PublishDomainEvent")
                .WithTags(tagName)
                .WithSummary("Publish a discovered domain event")
                .WithDescription("Deserializes and publishes a domain event by its discovered project-unit key.");

            if (Option.EnableRequestFilter)
            {
                endpoints.MapPost("/framework/request-filter",
                    async ([FromBody] RequestFilterDto dto,
                          [FromServices] ProjectUnitsFacade projectUnitsFacade) =>
                    {
                        return await projectUnitsFacade.ManageRequestFilterAsync(dto.Urls, dto.Disable);
                    })
                    .WithName("ProjectUnits.ConfigureRequestFilter")
                    .WithTags(tagName)
                    .WithSummary("Configure the project-unit request filter")
                    .WithDescription("Enables or disables host-local request paths when request filtering is configured.");
            }

            endpoints.MapGet("/framework/units",
                async ([FromServices] ProjectUnitsFacade projectUnitsFacade) =>
                {
                    return await projectUnitsFacade.GetAllProjectUnitsAsync();
                })
                .WithName("ProjectUnits.List")
                .WithTags(tagName)
                .WithSummary("List discovered project units")
                .WithDescription("Returns every architecture unit discovered for the current host.");

            endpoints.MapGet("/framework/units/dashboard",
                async ([FromServices] ProjectUnitsFacade projectUnitsFacade) =>
                {
                    return await projectUnitsFacade.GetDashboardAsync();
                })
                .WithName("ProjectUnits.Dashboard")
                .WithTags(tagName)
                .WithSummary("Get the project-unit status dashboard")
                .WithDescription("Returns host identity, architecture health, independent context coverage, and actionable gaps.");

            endpoints.MapGet("/framework/units/domain-event",
                async ([FromServices] ProjectUnitsFacade projectUnitsFacade) =>
                {
                    return await projectUnitsFacade.GetDomainEventsAsync();
                })
                .WithName("ProjectUnits.ListDomainEvents")
                .WithTags(tagName)
                .WithSummary("List discovered domain events")
                .WithDescription("Returns domain-event project units and their serializable structure.");

            endpoints.MapGet("/framework/units/{key}",
                async ([FromRoute] string key,
                      [FromServices] ProjectUnitsFacade projectUnitsFacade,
                      CancellationToken cancellationToken) =>
                {
                    return await projectUnitsFacade.GetProjectUnitDetailAsync(key, cancellationToken);
                })
                .WithName("ProjectUnits.Detail")
                .WithTags(tagName)
                .WithSummary("Get project-unit detail")
                .WithDescription("Returns typed project-unit metadata, dependencies, methods, and requirement links.");

            endpoints.MapGet("/framework/enum",
                async ([FromServices] ProjectUnitsFacade projectUnitsFacade,
                      [FromQuery] string? name = null) =>
                {
                    return await projectUnitsFacade.GetEnumInfoAsync(name);
                })
                .WithName("ProjectUnits.ListEnums")
                .WithTags(tagName)
                .WithSummary("List discovered enum metadata")
                .WithDescription("Returns discovered enum names and values, optionally filtered by enum name.");
        });
    }

    /// <summary>
    /// Uses the optional AutoControllers edge as the single source of truth for the derived CRUD service suffix.
    /// An explicit ProjectUnits convention remains authoritative.
    /// </summary>
    private ProjectUnitNamingOptions CreateEffectiveNamingOptions(IModuleOptionReader modules)
    {
        var configured = Option.ConventionOptions;
        var effective = new ProjectUnitNamingOptions
        {
            Dict = new Dictionary<EProjectUnitType, ProjectUnitNamingRule>(configured.Dict),
            EnableNameConvention = configured.EnableNameConvention,
            NameConventionMode = configured.NameConventionMode
        };

        if (!effective.Dict.ContainsKey(EProjectUnitType.CrudApplicationService)
            && modules.TryGet<ModuleAutoControllers, ModuleAutoControllersOption>(out var autoControllersOptions)
            && autoControllersOptions is { } configuredAutoControllers
            && !string.IsNullOrEmpty(configuredAutoControllers.Crud.CrudControllerPostfix))
        {
            effective.Dict[EProjectUnitType.CrudApplicationService] = new ProjectUnitNamingRule
            {
                Postfix = configuredAutoControllers.Crud.CrudControllerPostfix
            };
        }

        return effective;
    }
}

/// <summary>
/// Provides fluent registration for the ProjectUnits module.
/// </summary>
public static class ModuleProjectUnitsRegistrationExtensions
{
    /// <summary>
    /// Enables XML documentation analysis for discovered project units.
    /// </summary>
    /// <param name="module">The ProjectUnits module registration.</param>
    public static ModuleRegistration<ModuleProjectUnits, ModuleProjectUnitsOption> WithDocumentationDetails(
        this ModuleRegistration<ModuleProjectUnits, ModuleProjectUnitsOption> module)
    {
        module.Configure(options => options.ParseUnitDetails = true);
        module.Require<ModuleXmlDocumentation, ModuleXmlDocumentationOption>();
        return module;
    }

    /// <summary>
    /// Registers the application-owned resolver used to turn requirement identifiers into optional navigation links.
    /// </summary>
    /// <param name="module">The ProjectUnits module registration.</param>
    /// <typeparam name="TResolver">A scoped requirement-link resolver implementation.</typeparam>
    /// <returns>The current module registration.</returns>
    /// <remarks>
    /// Resolution occurs only when project-unit detail is requested. Unknown requirements should return
    /// <see langword="null"/> so they remain visible as unresolved references.
    /// </remarks>
    public static ModuleRegistration<ModuleProjectUnits, ModuleProjectUnitsOption> UseRequirementLinkResolver<TResolver>(this ModuleRegistration<ModuleProjectUnits, ModuleProjectUnitsOption> module)
        where TResolver : class, IProjectUnitRequirementLinkResolver
    {
        module.ConfigureServices(context =>
        {
            context.Services.Replace(
                ServiceDescriptor.Scoped<IProjectUnitRequirementLinkResolver, TResolver>());
        });
        return module;
    }

}

/// <summary>
/// Configures host-scoped project-unit discovery and diagnostic endpoints.
/// </summary>
public class ModuleProjectUnitsOption : MinimalApiModuleOptions<ModuleProjectUnits>
{
    /// <summary>
    /// Gets or sets the naming rules used to validate discovered architectural units.
    /// </summary>
    public ProjectUnitNamingOptions ConventionOptions { get; set; } = new();

    /// <summary>
    /// Gets or sets whether the request-filter middleware and its management endpoint are enabled.
    /// The filter is disabled by default and its path state is isolated to the current host.
    /// </summary>
    public bool EnableRequestFilter { get; set; }

    /// <summary>
    /// Gets or sets whether ProjectUnits loads XML summaries for discovered types and methods.
    /// The default is <see langword="false"/>. Enable it through
    /// <see cref="ModuleProjectUnitsRegistrationExtensions.WithDocumentationDetails"/> so the required XML
    /// documentation module is declared explicitly with the feature.
    /// </summary>
    public bool ParseUnitDetails { get; internal set; }
}

/// <summary>
/// Configures project-unit naming validation globally and per architectural category.
/// </summary>
public class ProjectUnitNamingOptions
{
    /// <summary>
    /// Gets or sets category-specific naming rules. Missing categories use their project-unit model defaults.
    /// </summary>
    public Dictionary<EProjectUnitType, ProjectUnitNamingRule> Dict { get; set; } = [];

    /// <summary>
    /// Gets or sets the fallback behavior when a naming rule is violated. The default records a warning.
    /// </summary>
    public ENameConventionMode NameConventionMode { get; set; } = ENameConventionMode.Warning;

    /// <summary>
    /// Gets or sets whether naming conventions are validated. Validation is disabled by default.
    /// </summary>
    public bool EnableNameConvention { get; set; }
}

/// <summary>
/// Describes the required fragments of a project-unit CLR type name and namespace.
/// </summary>
public class ProjectUnitNamingRule
{
    /// <summary>
    /// Gets or sets the required type-name suffix.
    /// </summary>
    public string? Postfix { get; set; }

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
    public ENameConventionMode? NameConventionMode { get; set; }

    public override string ToString()
    {
        return $"{Postfix?.Be("Suffix: {0}\n", true)}{Prefix?.Be("Prefix: {0}\n", true)}{Contains?.Be("Contains: {0}\n", true)}{NamespaceContains?.Be("Namespace contains: {0}", true)}".TrimEnd();
    }
}

/// <summary>
/// Specifies how ProjectUnits responds to naming-convention violations.
/// </summary>
public enum ENameConventionMode
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
