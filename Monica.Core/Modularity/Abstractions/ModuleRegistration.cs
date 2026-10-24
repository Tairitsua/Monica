using Microsoft.Extensions.Options;
using Monica.Core.Modularity.Models;
using Monica.Core.Modularity.Models.Internal;

namespace Monica.Core.Modularity.Abstractions;

/// <summary>
/// Represents a host-bound module declaration. Registrations collect configuration and lifecycle contributions
/// until the enclosing <c>AddMonica</c> owned-registration pass completes, then become permanently sealed.
/// </summary>
/// <typeparam name="TModule">The module strategy type.</typeparam>
/// <typeparam name="TOptions">The module's primary option type.</typeparam>
public sealed class ModuleRegistration<TModule, TOptions>
    where TModule : MonicaModule<TOptions>, new()
    where TOptions : ModuleOptions<TModule>, new()
{
    private readonly MonicaApplication _application;
    private readonly Type? _configuredBy;
    private readonly Type? _registrationOwner;

    internal ModuleRegistration(MonicaApplication application, Type? configuredBy)
        : this(application, configuredBy, application.Composition.RegistrationOwner)
    {
    }

    internal ModuleRegistration(MonicaApplication application, Type? configuredBy, Type? registrationOwner)
    {
        _application = application;
        _configuredBy = configuredBy;
        _registrationOwner = registrationOwner;
        _application.Modules.GetOrAddRegistration<TModule, TOptions>();
    }

    /// <summary>
    /// Adds a host or feature option contribution. Contributions execute once after dependency defaults,
    /// in the order in which they were recorded.
    /// </summary>
    public ModuleRegistration<TModule, TOptions> Configure(Action<TOptions>? configure)
    {
        if (configure is not null)
        {
            _application.Modules.AddOptionContribution<TModule, TOptions>(_configuredBy, configure, _registrationOwner);
        }

        return this;
    }

    /// <summary>
    /// Includes a companion module without creating a dependency or ordering edge.
    /// </summary>
    public ModuleRegistration<TCompanion, TCompanionOptions> Include<TCompanion, TCompanionOptions>(
        Action<TCompanionOptions>? configure = null)
        where TCompanion : MonicaModule<TCompanionOptions>, new()
        where TCompanionOptions : ModuleOptions<TCompanion>, new()
    {
        _application.Modules.EnsureRegistrationIsMutable(typeof(TModule));
        var registration = new ModuleRegistration<TCompanion, TCompanionOptions>(
            _application,
            configuredBy: typeof(TModule), _registrationOwner);
        return registration.Configure(configure);
    }

    /// <summary>
    /// Includes and hard-requires another module, returning its registration for explicit feature configuration.
    /// </summary>
    public ModuleRegistration<TDependency, TDependencyOptions> Require<TDependency, TDependencyOptions>(
        Action<TDependencyOptions>? configure = null)
        where TDependency : MonicaModule<TDependencyOptions>, new()
        where TDependencyOptions : ModuleOptions<TDependency>, new()
    {
        return _application.Modules.Require<TDependency, TDependencyOptions>(typeof(TModule), configure, _registrationOwner);
    }

    /// <summary>
    /// Orders this module after another module only when the target is already included.
    /// </summary>
    public ModuleRegistration<TModule, TOptions> AfterIfPresent<TDependency, TDependencyOptions>()
        where TDependency : MonicaModule<TDependencyOptions>, new()
        where TDependencyOptions : ModuleOptions<TDependency>, new()
    {
        _application.Modules.AfterIfPresent(typeof(TModule), typeof(TDependency));
        return this;
    }

    /// <summary>
    /// Records a service contribution for this module.
    /// </summary>
    public ModuleRegistration<TModule, TOptions> ConfigureServices(
        Action<ModuleContext<TOptions>> configure,
        ModuleRegistrationOrder order = ModuleRegistrationOrder.AfterModule)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ValidateOrder(order);
        AddRequest(
            ModulePhase.ConfigureServices,
            (int)order,
            context => configure(new ModuleContext<TOptions>(context)));
        return this;
    }

    /// <summary>
    /// Records a post-discovery service contribution for this module.
    /// </summary>
    public ModuleRegistration<TModule, TOptions> PostConfigureServices(
        Action<ModuleContext<TOptions>> configure,
        ModuleRegistrationOrder order = ModuleRegistrationOrder.AfterModule)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ValidateOrder(order);
        AddRequest(
            ModulePhase.PostConfigureServices,
            (int)order,
            context => configure(new ModuleContext<TOptions>(context)));
        return this;
    }

    /// <summary>
    /// Records a host-builder contribution for this module.
    /// </summary>
    public ModuleRegistration<TModule, TOptions> ConfigureBuilder(
        Action<ModuleBuilderContext<TOptions>> configure,
        ModuleRegistrationOrder order = ModuleRegistrationOrder.AfterModule)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ValidateOrder(order);
        AddRequest(
            ModulePhase.ConfigureBuilder,
            (int)order,
            context => configure(new ModuleBuilderContext<TOptions>(context)));
        return this;
    }

    /// <summary>
    /// Records an ASP.NET Core middleware contribution for this module.
    /// </summary>
    public ModuleRegistration<TModule, TOptions> ConfigureApplicationBuilder(
        Action<WebModuleContext<TOptions>> configure,
        ModuleWebStage stage = ModuleWebStage.BeforeRouting)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureWebCapability();
        _application.Modules
            .GetOrAddRegistration<TModule, TOptions>()
            .AddApplicationBuilderContributionRequest(
                stage,
                context => configure(new WebModuleContext<TOptions>(context)), _registrationOwner);
        return this;
    }

    /// <summary>
    /// Records an endpoint contribution for this module.
    /// </summary>
    public ModuleRegistration<TModule, TOptions> ConfigureEndpoints(
        Action<WebModuleContext<TOptions>> configure,
        ModuleRegistrationOrder order = ModuleRegistrationOrder.AfterModule)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureWebCapability();
        ValidateOrder(order);
        AddRequest(
            ModulePhase.ConfigureEndpoints,
            (int)order,
            context => configure(new WebModuleContext<TOptions>(context)));
        return this;
    }

    /// <summary>
    /// Records an externally discoverable keyed service identifier owned by this module.
    /// </summary>
    public ModuleRegistration<TModule, TOptions> RecordKeyedServiceKey(string serviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        _application.Modules.GetOrAddRegistration<TModule, TOptions>().AddKeyedServiceKey(serviceKey);
        return this;
    }

    /// <summary>
    /// Marks an explicitly required feature as configured.
    /// </summary>
    public ModuleRegistration<TModule, TOptions> SatisfyFeature(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        _application.Modules.SatisfyFeature(typeof(TModule), featureName);
        return this;
    }

    /// <summary>
    /// Declares a feature requirement introduced by an explicitly selected composition path.
    /// </summary>
    /// <param name="featureName">The stable feature name used in validation diagnostics.</param>
    /// <remarks>
    /// Use this from rich registration extensions when a sub-feature, rather than the module itself, requires a
    /// provider or strategy. The requirement is order-independent and is validated after all host contributions have
    /// been collected.
    /// </remarks>
    public ModuleRegistration<TModule, TOptions> RequireFeature(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        _application.Modules.RequireFeature(typeof(TModule), featureName);
        return this;
    }

    /// <summary>
    /// Declares that an explicitly selected web feature requires the ASP.NET Core host adapter.
    /// </summary>
    /// <param name="reason">An actionable explanation included in preflight and runtime diagnostics.</param>
    /// <remarks>
    /// Use this for feature-level middleware or endpoint contributions on an otherwise generic-host-compatible
    /// <see cref="IWebModule"/>. Intrinsically web-only module types implement <see cref="IWebHostRequiredModule"/>.
    /// </remarks>
    public ModuleRegistration<TModule, TOptions> RequireWebHost(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureWebCapability();
        _application.Modules.GetOrAddRegistration<TModule, TOptions>().RequireWebHost(reason);
        return this;
    }

    /// <summary>
    /// Omits this module and every module that hard-depends on it before host mutation begins.
    /// </summary>
    public ModuleRegistration<TModule, TOptions> Disable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _application.Modules.Disable(typeof(TModule), reason);
        return this;
    }

    /// <summary>
    /// Adds a startup-frozen named option profile for a keyed provider instance.
    /// </summary>
    public ModuleRegistration<TModule, TOptions> ConfigureProfile(
        string name,
        Action<TOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        _application.Modules.AddProfileContribution<TModule, TOptions>(name, configure, _registrationOwner);
        return this;
    }

    /// <summary>
    /// Gets a named, finalized option profile for capture by a keyed service registration.
    /// This method is valid only from a composition contribution after options have been compiled.
    /// </summary>
    public TOptions GetProfile(string name)
    {
        return _application.Modules.GetProfile<TModule, TOptions>(name);
    }

    private void AddRequest(
        ModulePhase phase,
        int order,
        Action<ModuleConfigurationContext> configure)
    {
        _application.Modules
            .GetOrAddRegistration<TModule, TOptions>()
            .AddContributionRequest(phase, order, configure, _registrationOwner);
    }

    private static void EnsureWebCapability()
    {
        if (!typeof(IWebModule).IsAssignableFrom(typeof(TModule)))
        {
            throw new InvalidOperationException(
                $"Module {typeof(TModule).Name} must implement {nameof(IWebModule)} before it can declare Web contributions.");
        }
    }

    private static void ValidateOrder(ModuleRegistrationOrder order)
    {
        if (!Enum.IsDefined(order))
        {
            throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown module registration order.");
        }
    }
}
