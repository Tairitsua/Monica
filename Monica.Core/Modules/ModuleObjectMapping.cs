using Mapster;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.TypeDiscovery.Models;
using Monica.Core.ObjectMapping.Abstractions;
using Monica.Core.ObjectMapping.Facades;
using Monica.Core.ObjectMapping.Providers.Mapster;
using Monica.Core.ObjectMapping.Services;
using Monica.Core.Results;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleObjectMappingBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers one host-owned Mapster configuration and automatically applies concrete <see cref="IRegister"/>
        /// profiles found by Monica's business-type discovery pipeline.
        /// </summary>
        /// <param name="action">Optional configuration for the object-mapping endpoints.</param>
        /// <returns>The host-bound object-mapping registration.</returns>
        /// <remarks>
        /// Profiles outside the host's type-discovery scope can be added explicitly through
        /// <c>AddProfile&lt;TProfile&gt;()</c>.
        /// </remarks>
        public ModuleRegistration<ModuleObjectMapping, ModuleObjectMappingOption> AddObjectMapping(
            Action<ModuleObjectMappingOption>? action = null)
        {
            return builder.AddModule<ModuleObjectMapping, ModuleObjectMappingOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleObjectMapping, ModuleObjectMappingOption> registration)
    {
        /// <summary>
        /// Adds a Mapster profile to the current host's object-mapping configuration.
        /// </summary>
        public ModuleRegistration<ModuleObjectMapping, ModuleObjectMappingOption> AddProfile<TProfile>()
            where TProfile : class, IRegister
        {
            return registration.Configure(options => options.AddProfile(typeof(TProfile)));
        }
    }
}

/// <summary>
/// Provides the Mapster-based object mapping capability.
/// </summary>
/// <remarks>
/// Each Monica host owns an isolated configuration. Concrete, closed <see cref="IRegister"/> profiles discovered as
/// business types are composed after explicitly registered profiles in dependency-first assembly order.
/// </remarks>
public class ModuleObjectMapping : MonicaModule<ModuleObjectMappingOption>, IWebModule
{
    private readonly MapsterConfigurationRuntime _mappingRuntime = new();
    private readonly MapsterProfileCatalog _profileCatalog = new();

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleObjectMappingOption> context)
    {
        var services = context.Services;
        services.AddSingleton(_profileCatalog);
        services.AddSingleton(_mappingRuntime);
        services.AddScoped<IObjectMapper, MapsterObjectMapper>();
        services.AddSingleton<MapsterMappingInspector>();
        services.AddScoped<ObjectMappingStatusService>();
        services.AddScoped<ObjectMappingFacade>(serviceProvider => new ObjectMappingFacade(
            serviceProvider.GetRequiredService<ObjectMappingStatusService>(),
            serviceProvider.GetRequiredService<ILogger<ObjectMappingFacade>>()));
    }

    /// <inheritdoc />
    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleObjectMappingOption> discovery)
    {
        discovery.Match(
            TypeQuery.ClosedClass.AssignableTo<IRegister>(),
            (_, matches) =>
            {
                foreach (var match in matches)
                {
                    _profileCatalog.Discover(match.Type);
                }
            });
    }

    /// <inheritdoc />
    public override void PostConfigureServices(ModuleContext<ModuleObjectMappingOption> context)
    {
        var compilationBarrier = Option.GetCompilationBarrier();
        _mappingRuntime.Configure(config =>
            _profileCatalog.ApplyProfiles(config, Option.ProfileTypes, Application.TypeDependencyOrderer));
        var compilationCandidate = _mappingRuntime.FreezeAndCreateCompilationCandidate();

        ScheduleStartupWork(
            "compile-mapster-configuration",
            () =>
            {
                compilationCandidate.Compile(failFast: Option.CompileFailFast);
                _mappingRuntime.PublishCompiled(compilationCandidate);
            },
            compilationBarrier);
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleObjectMappingOption> context)
    {
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet("/mapper/status", async (HttpContext context, ObjectMappingFacade facade) =>
            {
                var result = await facade.GetStatusAsync();
                if (result.IsFailure())
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new { error = result.WithoutData() });
                    return;
                }

                var data = result.Data ?? throw new InvalidOperationException("Object mapping status returned no data.");
                var res = new
                {
                    count = data.Count,
                    cards = data.Mappings.Select(x => new
                    {
                        x.SourceType,
                        x.DestinationType,
                        x.MapExpression
                    })
                };
                await context.Response.WriteAsJsonAsync(res);
            })
            .WithName("GetObjectMappingStatus")
            .WithTags(tagName)
            .WithSummary("Gets object mapping status")
            .WithDescription("Returns the mapping pairs and generated Mapster expressions owned by this Monica host.");
        });
    }
}

/// <summary>
/// Configures the Mapster object mapping runtime for one Monica host.
/// </summary>
public class ModuleObjectMappingOption : MinimalApiModuleOptions<ModuleObjectMapping>
{
    private readonly HashSet<string> _profileKeys = new(StringComparer.Ordinal);
    private readonly List<Type> _profileTypes = [];

    /// <summary>
    /// Gets or sets whether Mapster's <see cref="TypeAdapterConfig.Compile(bool)"/> should throw immediately on the
    /// first invalid mapping pair instead of collecting all errors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see langword="true"/> (the default), compilation aborts on the first invalid pair and the offending
    /// mapping is surfaced directly in the exception. This gives the fastest feedback during development and CI.
    /// </para>
    /// <para>
    /// When <see langword="false"/>, Mapster collects every invalid pair and throws a single aggregate exception.
    /// Use this to review all mapping problems in one pass.
    /// </para>
    /// </remarks>
    public bool CompileFailFast { get; set; } = true;

    /// <summary>
    /// Gets or sets the startup barrier that governs eager Mapster compilation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <see cref="ModuleStartupWorkBarrier.NoBarrier"/>. The host can become ready immediately and
    /// use the uncompiled host-owned configuration lazily while Monica compiles an isolated clone in the background.
    /// A compilation failure remains observable through module diagnostics and leaves lazy mapping available.
    /// </para>
    /// <para>
    /// Select <see cref="ModuleStartupWorkBarrier.BeforeHostLifecycle"/> in CI or strict hosts when every mapping must
    /// pass eager validation before any hosted lifecycle participant starts. ObjectMapping intentionally exposes only
    /// these readiness-critical and non-blocking strategies; composition-phase barriers are not supported.
    /// </para>
    /// </remarks>
    public ModuleStartupWorkBarrier CompilationBarrier { get; set; } = ModuleStartupWorkBarrier.NoBarrier;

    /// <summary>
    /// Gets the explicitly registered mapping profiles in deterministic composition order.
    /// </summary>
    internal IReadOnlyList<Type> ProfileTypes => _profileTypes;

    /// <summary>
    /// Validates and returns the ObjectMapping compilation strategy.
    /// </summary>
    /// <returns>The configured non-blocking or host-lifecycle barrier.</returns>
    internal ModuleStartupWorkBarrier GetCompilationBarrier()
    {
        if (CompilationBarrier is ModuleStartupWorkBarrier.NoBarrier
            or ModuleStartupWorkBarrier.BeforeHostLifecycle)
        {
            return CompilationBarrier;
        }

        throw new InvalidOperationException(
            $"{nameof(CompilationBarrier)} only supports {ModuleStartupWorkBarrier.NoBarrier} or " +
            $"{ModuleStartupWorkBarrier.BeforeHostLifecycle}, but '{CompilationBarrier}' was configured.");
    }

    /// <summary>
    /// Records one mapping profile while preserving first-registration order.
    /// </summary>
    /// <param name="profileType">The concrete Mapster profile type.</param>
    internal void AddProfile(Type profileType)
    {
        var profileKey = profileType.AssemblyQualifiedName
            ?? throw new InvalidOperationException(
                $"Object-mapping profile '{profileType.FullName}' does not have an assembly-qualified type name.");

        if (_profileKeys.Add(profileKey))
        {
            _profileTypes.Add(profileType);
        }
    }
}
