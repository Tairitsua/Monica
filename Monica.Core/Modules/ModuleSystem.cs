using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Facades;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Diagnostics.Services;
using Monica.Core.Modularity.Metrics;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleSystemBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the module system diagnostics module.
        /// </summary>
        public ModuleRegistration<ModuleSystem, ModuleSystemOption> AddModuleSystem(
            Action<ModuleSystemOption>? action = null)
        {
            return builder.AddModule<ModuleSystem, ModuleSystemOption>(action);
        }
    }
}

/// <summary>
/// Module system diagnostics module.
/// Registers inspection services and the host-facing diagnostics facade.
/// </summary>
public class ModuleSystem : MonicaModule<ModuleSystemOption>
{
    /// <summary>
    /// Registers diagnostics services for the module system.
    /// </summary>
    public override void ConfigureServices(ModuleContext<ModuleSystemOption> context)
    {
        var services = context.Services;
        services.AddSingleton<ModuleDiagnosticsService>();
        services.AddSingleton(static provider => new ModuleDiagnosticsFacade(
            provider.GetRequiredService<ModuleDiagnosticsService>(),
            provider.GetRequiredService<ModuleInitMetrics>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ModuleDiagnosticsFacade>>()));
        services.TryAddSingleton<ModuleInitMetrics>();
        services.AddHostedService<ModuleInitMetricsActivationService>();
        context.Composition.AdmitHostedLifecycle(services.Last(descriptor => !descriptor.IsKeyedService && descriptor.ImplementationType == typeof(ModuleInitMetricsActivationService)), typeof(ModuleInitMetricsActivationService));
    }
}

/// <summary>
/// Options for the module system diagnostics module.
/// </summary>
public class ModuleSystemOption : ModuleOptions<ModuleSystem>
{
    private readonly Dictionary<Type, ModuleOptionDiagnosticsPolicyDefinition> _optionDiagnosticsPolicies = [];

    /// <summary>
    /// Adds host-owned sensitivity rules to the automatic bounded diagnostics for one module. Every public,
    /// non-indexed option property is cataloged automatically; registration is needed only when an attribute or the
    /// built-in sensitive-name classification is insufficient.
    /// </summary>
    /// <typeparam name="TModule">The module that owns the options.</typeparam>
    /// <typeparam name="TOptions">The module's concrete option type.</typeparam>
    /// <param name="configure">Marks direct or nested properties as sensitive.</param>
    /// <returns>This option object.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the same module is configured more than once.</exception>
    public ModuleSystemOption ConfigureModuleOptionDiagnostics<TModule, TOptions>(
        Action<ModuleOptionDiagnosticsPolicy<TOptions>> configure)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        ArgumentNullException.ThrowIfNull(configure);
        var policy = new ModuleOptionDiagnosticsPolicy<TOptions>();
        configure(policy);
        if (!_optionDiagnosticsPolicies.TryAdd(typeof(TModule), policy.Build()))
        {
            throw new InvalidOperationException(
                $"Module option diagnostics policy for {typeof(TModule).Name} was configured more than once.");
        }

        return this;
    }

    internal ModuleOptionDiagnosticsPolicyDefinition GetOptionDiagnosticsPolicy(Type moduleType) =>
        _optionDiagnosticsPolicies.GetValueOrDefault(moduleType)
        ?? ModuleOptionDiagnosticsPolicyDefinition.Empty;
}
