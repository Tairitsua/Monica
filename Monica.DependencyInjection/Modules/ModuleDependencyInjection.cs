using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.TypeDiscovery.Models;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.DependencyInjection.Annotations;
using Monica.DependencyInjection.Facades;
using Monica.DependencyInjection.Services;
using Monica.DependencyInjection.Services.Support;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleDependencyInjectionBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Enables Monica conventional dependency registration and cached service-provider access.
        /// </summary>
        public ModuleRegistration<ModuleDependencyInjection, ModuleDependencyInjectionOption> AddDependencyInjection(Action<ModuleDependencyInjectionOption>? action = null)
        {
            return builder.AddModule<ModuleDependencyInjection, ModuleDependencyInjectionOption>(action);
        }
    }
}

public class ModuleDependencyInjection : MonicaModule<ModuleDependencyInjectionOption>
{
    private DependencyInjectionDiagnosticsRegistry? _diagnosticsRegistry;

    public override void ConfigureServices(ModuleContext<ModuleDependencyInjectionOption> context)
    {
        var services = context.Services;
        if (Option.EnableAutoRegistrationDiagnostics)
        {
            _diagnosticsRegistry ??= new DependencyInjectionDiagnosticsRegistry();
            _diagnosticsRegistry.BindServices(services);

            services.AddSingleton(_diagnosticsRegistry);
            services.AddSingleton<DependencyInjectionDiagnosticsService>();
            services.AddSingleton<DependencyInjectionDiagnosticsFacade>();
            services.AddHostedService<DependencyInjectionDiagnosticsHostedService>();
        }

        services.AddScoped<ICachedServiceProvider, CachedServiceProvider>();
    }

    /// <inheritdoc />
    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleDependencyInjectionOption> discovery)
    {
        var dependencyCandidates = TypeQuery.AnyOf(
            TypeQuery.All.AssignableTo<ITransientDependency>(),
            TypeQuery.All.AssignableTo<ISingletonDependency>(),
            TypeQuery.All.AssignableTo<IScopedDependency>(),
            TypeQuery.All.HasAttribute<DependencyAttribute>(inherit: true));

        discovery.Match(
            TypeQuery.ConcreteClass.And(dependencyCandidates),
            (context, matches) =>
            {
                // The serial commit runs after ConfigureServices, when diagnostics has been initialized and bound.
                var registrar = new ConventionalRegistrar(Option, Logger, _diagnosticsRegistry);
                foreach (var match in matches)
                {
                    if (Application.TypeActivation?.IsEnabled(match.Type, TypeActivationKind.Service) == false) continue;
                    registrar.AddType(context.Registrations, match);
                }
            });
    }
}

/// <summary>
/// Configures the Monica dependency-injection module.
/// </summary>
public static class ModuleDependencyInjectionRegistrationExtensions
{
    /// <summary>
    /// Enables or disables Monica automatic-registration diagnostics.
    /// </summary>
    /// <param name="enabled">
    /// <see langword="true"/> to capture the diagnostics snapshot and emit startup logs for automatic registration;
    /// otherwise, <see langword="false"/>.
    /// </param>
    /// <param name="module">The DependencyInjection module registration.</param>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleDependencyInjection, ModuleDependencyInjectionOption> EnableAutoRegistrationDiagnostics(this ModuleRegistration<ModuleDependencyInjection, ModuleDependencyInjectionOption> module, bool enabled = true)
    {
        module.Configure(options => options.EnableAutoRegistrationDiagnostics = enabled);
        if (enabled)
        {
            module.Require<ModuleHostedService, ModuleHostedServiceOption>();
        }

        return module;
    }

    /// <summary>
    /// Enables or disables the per-type automatic-registration startup logs.
    /// </summary>
    /// <param name="enabled">
    /// <see langword="true"/> to emit a log entry for every auto-registered type during startup;
    /// otherwise, <see langword="false"/>. Disabled by default to keep startup output quiet.
    /// </param>
    /// <param name="module">The DependencyInjection module registration.</param>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleDependencyInjection, ModuleDependencyInjectionOption> EnableAutoRegistrationLogging(this ModuleRegistration<ModuleDependencyInjection, ModuleDependencyInjectionOption> module, bool enabled = true)
    {
        module.Configure(options => options.EnableAutoRegistrationLogging = enabled);
        return module;
    }

}

/// <summary>
/// Configures Monica conventional dependency registration behavior.
/// </summary>
public class ModuleDependencyInjectionOption : ModuleOptions<ModuleDependencyInjection>
{
    /// <summary>
    /// Gets or sets a value indicating whether Monica should capture automatic-registration diagnostics.
    /// </summary>
    /// <remarks>
    /// When enabled, Monica emits startup diagnostic logs for automatic service registration and captures the
    /// diagnostics snapshot consumed by the dependency-injection UI module.
    /// Leave this disabled when diagnostics are not needed so startup registration avoids the extra tracking overhead.
    /// </remarks>
    public bool EnableAutoRegistrationDiagnostics { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Monica writes a startup log entry for each auto-registered type.
    /// </summary>
    /// <remarks>
    /// This only controls the per-type startup logs (concrete-only warnings, registration info, and failures).
    /// It is independent of <see cref="EnableAutoRegistrationDiagnostics"/>, which captures the snapshot consumed by
    /// the dependency-injection UI module. Disabled by default to keep startup output quiet even when the UI page is on.
    /// </remarks>
    public bool EnableAutoRegistrationLogging { get; set; }
}
