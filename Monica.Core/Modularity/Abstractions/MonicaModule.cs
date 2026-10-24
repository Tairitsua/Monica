using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Monica.Core.Modularity.Models;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models.Internal;
using Monica.Core.TypeDiscovery.Models;

namespace Monica.Core.Modularity.Abstractions;

/// <summary>
/// Defines one host-owned Monica module strategy. Module instances are created exactly once while the
/// composition graph is compiled and are never resolved from dependency injection.
/// </summary>
public abstract class MonicaModule : IModule
{
    private MonicaApplication? _application;

    /// <summary>
    /// Gets the application that owns this module instance.
    /// </summary>
    protected MonicaApplication Application =>
        _application
        ?? throw new InvalidOperationException(
            $"{GetType().Name} is not available until module options have been finalized.");

    /// <summary>
    /// Gets a composition logger whose category is the concrete module type.
    /// </summary>
    protected ILogger Logger => Application.CreateLogger(GetType());

    /// <summary>
    /// Gets the optional diagnostic key derived for this module type.
    /// </summary>
    public ModuleKey ModuleKey => Application.Dependencies.ResolveModuleKey(GetType());

    /// <summary>
    /// Declares intrinsic hard dependencies and optional ordering relationships.
    /// This callback executes once before this module's options are materialized.
    /// </summary>
    /// <param name="module">The declaration surface for the current module.</param>
    public virtual void Describe(ModuleDescriptor module)
    {
    }

    internal abstract void BindOptions(MonicaApplication application, object options);

    internal abstract void ValidateFinalOptions(object options, string? profileName);

    internal abstract void DeclareModuleContracts(ModuleRegistrationState registration);

    internal abstract void AttachLifecycle(ModuleRegistrationState registration);

    internal abstract ITypeDiscoveryPlan DeclareTypeDiscoveryPlan();

    internal abstract bool IsWebModule { get; }

    internal abstract bool RequiresWebHost { get; }

    internal void BindApplication(MonicaApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (_application is not null)
        {
            throw new InvalidOperationException($"{GetType().Name} was already bound to a host.");
        }

        _application = application;
    }

    /// <summary>
    /// Replaces the logger factory used by later composition callbacks for this host.
    /// </summary>
    /// <param name="factory">The host-owned logger factory.</param>
    protected void UseCompositionLoggerFactory(ILoggerFactory factory)
    {
        Application.ReplaceCompositionLoggerFactory(factory);
    }

    /// <summary>
    /// Reads the finalized options of a directly required module.
    /// </summary>
    /// <typeparam name="TModule">The required module type.</typeparam>
    /// <typeparam name="TOptions">The required module option type.</typeparam>
    /// <returns>The finalized host-owned option object.</returns>
    protected TOptions GetOptions<TModule, TOptions>()
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        return Application.Modules.GetRequiredOptions<TModule, TOptions>(GetType());
    }

    /// <summary>
    /// Attempts to read the finalized options of a directly ordered module when that module is active.
    /// </summary>
    /// <typeparam name="TModule">The optionally ordered module type.</typeparam>
    /// <typeparam name="TOptions">The optional module's option type.</typeparam>
    /// <param name="options">The finalized option object when the module is present.</param>
    /// <returns><see langword="true"/> when the optional module is active.</returns>
    protected bool TryGetOptions<TModule, TOptions>(out TOptions? options)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        return Application.Modules.TryGetOrderedOptions<TModule, TOptions>(GetType(), out options);
    }

    /// <summary>
    /// Determines whether a module type belongs to the current compiled host graph.
    /// </summary>
    /// <typeparam name="TModule">The module type to inspect.</typeparam>
    /// <returns><see langword="true"/> when the module is active.</returns>
    protected bool IsModuleRegistered<TModule>() where TModule : MonicaModule
    {
        return Application.Modules.IsRegistered(typeof(TModule));
    }

    /// <summary>
    /// Schedules isolated synchronous work on Monica's bounded startup scheduler.
    /// </summary>
    protected void ScheduleStartupWork(
        string name,
        Action work,
        ModuleStartupWorkBarrier barrier = ModuleStartupWorkBarrier.BeforeServiceRegistrationCompletion)
    {
        Application.Modules.ScheduleStartupWork(this, name, work, commit: null, barrier);
    }

    /// <summary>
    /// Schedules isolated synchronous work followed by a deterministic serial commit.
    /// </summary>
    protected void ScheduleStartupWork(
        string name,
        Action work,
        Action commit,
        ModuleStartupWorkBarrier barrier = ModuleStartupWorkBarrier.BeforeServiceRegistrationCompletion)
    {
        Application.Modules.ScheduleStartupWork(this, name, work, commit, barrier);
    }
}

/// <summary>
/// Defines a module with one primary, startup-frozen option object.
/// </summary>
/// <typeparam name="TOptions">The module-owned option type.</typeparam>
public abstract class MonicaModule<TOptions> : MonicaModule
    where TOptions : class, IModuleOptions, new()
{
    private const int MODULE_OWNED_REQUEST_ORDER = -1;
    private TOptions? _options;

    /// <summary>
    /// Gets the module's finalized option object. The same object is exposed through <c>IOptions&lt;TOptions&gt;</c>.
    /// </summary>
    protected TOptions Option =>
        _options
        ?? throw new InvalidOperationException(
            $"{GetType().Name} options are unavailable while Describe is executing.");

    /// <summary>
    /// Validates a finalized option object before Monica mutates the host builder or service collection.
    /// </summary>
    /// <param name="options">The frozen default options or one frozen named profile.</param>
    /// <param name="profileName">
    /// The named profile being validated, or <see langword="null"/> for the module's default options.
    /// </param>
    /// <remarks>
    /// Throw <see cref="InvalidOperationException"/> or <see cref="ArgumentException"/> with an actionable message
    /// when composition is invalid. Validation runs dependency-first and may read options only through relationships
    /// declared by <see cref="MonicaModule.Describe(ModuleDescriptor)"/>.
    /// </remarks>
    public virtual void ValidateOptions(TOptions options, string? profileName)
    {
    }

    /// <summary>
    /// Declares service contracts selected by this module's finalized options.
    /// </summary>
    /// <param name="contracts">The read-only finalized contract declaration surface.</param>
    /// <remarks>
    /// Use this callback for option-dependent unkeyed or keyed service requirements. Monica validates the declared
    /// identities after all service contributions complete. Do not resolve services or perform runtime work here.
    /// </remarks>
    public virtual void DeclareContracts(ModuleContractDescriptor<TOptions> contracts)
    {
    }

    /// <summary>
    /// Configures the host builder for this module.
    /// </summary>
    public virtual void ConfigureBuilder(ModuleBuilderContext<TOptions> context)
    {
    }

    /// <summary>
    /// Registers the module's ordinary services.
    /// </summary>
    public virtual void ConfigureServices(ModuleContext<TOptions> context)
    {
    }

    /// <summary>
    /// Declares structural business-type queries and their serial registration commits.
    /// </summary>
    public virtual void DeclareTypeDiscovery(TypeDiscoveryPlan<TOptions> discovery)
    {
    }

    /// <summary>
    /// Registers services that depend on completed business-type discovery.
    /// </summary>
    public virtual void PostConfigureServices(ModuleContext<TOptions> context)
    {
    }

    /// <summary>
    /// Configures the ASP.NET Core middleware pipeline for a web-capable module.
    /// </summary>
    public virtual void ConfigureApplicationBuilder(WebModuleContext<TOptions> context)
    {
    }

    /// <summary>
    /// Maps endpoints for a web-capable module.
    /// </summary>
    public virtual void ConfigureEndpoints(WebModuleContext<TOptions> context)
    {
    }

    /// <summary>
    /// Gets the named routing boundary for module-owned middleware configuration.
    /// </summary>
    protected virtual ModuleWebStage GetApplicationBuilderStage() => ModuleWebStage.BeforeRouting;

    internal sealed override void BindOptions(MonicaApplication application, object options)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(options);

        if (_options is not null)
        {
            throw new InvalidOperationException($"{GetType().Name} was already bound to a host.");
        }

        _options = (TOptions)options;
        BindApplication(application);
    }

    internal sealed override void ValidateFinalOptions(object options, string? profileName)
    {
        ValidateOptions((TOptions)options, profileName);
    }

    internal sealed override void DeclareModuleContracts(ModuleRegistrationState registration)
    {
        DeclareContracts(new ModuleContractDescriptor<TOptions>(registration));
    }

    internal sealed override void AttachLifecycle(ModuleRegistrationState registration)
    {
        registration.AddLifecycleRequest(
            ModulePhase.ConfigureBuilder,
            MODULE_OWNED_REQUEST_ORDER,
            context => ConfigureBuilder(new ModuleBuilderContext<TOptions>(context)));
        registration.AddLifecycleRequest(
            ModulePhase.ConfigureServices,
            MODULE_OWNED_REQUEST_ORDER,
            context => ConfigureServices(new ModuleContext<TOptions>(context)));
        registration.AddLifecycleRequest(
            ModulePhase.PostConfigureServices,
            MODULE_OWNED_REQUEST_ORDER,
            context => PostConfigureServices(new ModuleContext<TOptions>(context)));

        if (!IsWebModule)
        {
            return;
        }

        registration.AddApplicationBuilderLifecycleRequest(
            GetApplicationBuilderStage(),
            MODULE_OWNED_REQUEST_ORDER,
            context => ConfigureApplicationBuilder(new WebModuleContext<TOptions>(context)));
        registration.AddLifecycleRequest(
            ModulePhase.ConfigureEndpoints,
            MODULE_OWNED_REQUEST_ORDER,
            context => ConfigureEndpoints(new WebModuleContext<TOptions>(context)));
    }

    internal sealed override ITypeDiscoveryPlan DeclareTypeDiscoveryPlan()
    {
        var discovery = new TypeDiscoveryPlan<TOptions>();
        DeclareTypeDiscovery(discovery);
        return discovery;
    }

    internal sealed override bool IsWebModule => this is IWebModule;

    internal sealed override bool RequiresWebHost => this is IWebHostRequiredModule;

    /// <summary>
    /// Maps a grouped endpoint surface only when the module's Minimal API option is enabled.
    /// </summary>
    protected void UseEndpoints(WebModuleContext<TOptions> context, Action<IEndpointRouteBuilder> configure)
    {
        if (Option is IMinimalApiModuleOptions option && !option.GetIsMinimalApiEnabled())
        {
            return;
        }

        context.ApplicationBuilder.UseEndpoints(endpoints =>
        {
            var monicaEndpoints = endpoints.MapGroup(string.Empty).WithMonicaEndpoint();
            configure(monicaEndpoints);
        });
    }
}
