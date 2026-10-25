using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.ApplicationModel.Models;
using Monica.ApplicationModel.Services;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;

namespace Monica.Modules;

/// <summary>Composes application ownership and this executable's implementations with deployment configuration.</summary>
public static class ModuleApplicationModelBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Validates ownership and freezes role activation before business registrations are committed.</summary>
        public ModuleRegistration<ModuleApplicationModel, ModuleApplicationModelOption> AddApplicationModel(
            Action<ModuleApplicationModelOption>? configure = null) => builder.AddModule<ModuleApplicationModel, ModuleApplicationModelOption>(configure);
    }
}

/// <summary>Owns domain, role, operation, and logical application identity relationships.</summary>
public sealed class ModuleApplicationModel : MonicaModule<ModuleApplicationModelOption>, IApplicationCompositionModule
{
    private CompiledApplicationModel? _model;
    private IReadOnlyList<ApplicationApiClientDefinition> _clients = [];
    private IReadOnlyList<ApplicationRegistrationPlan> _registrations = [];
    private bool _registered;

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module) => module.Require<ModuleDomainCatalog, ModuleDomainCatalogOption>();

    /// <inheritdoc />
    public void PrepareApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder)
    {
        if (_model is not null) throw new InvalidOperationException("The application model has already been prepared.");
        var providers = Application.TypeFinder.GetAssemblies()
            .Select(assembly => assembly.GetCustomAttribute<GeneratedApplicationManifestAttribute>()?.ProviderType)
            .OfType<Type>().ToArray();
        var provider = Option.ManifestType ?? (providers.Length == 1 ? providers[0] :
            throw new InvalidOperationException($"Expected one generated application manifest, found {providers.Length}. Mark the executable with [assembly: ApplicationHost]."));
        if (!typeof(IApplicationManifest).IsAssignableFrom(provider) || provider.IsAbstract || provider.ContainsGenericParameters
            || provider.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException($"Manifest '{provider}' must be a concrete IApplicationManifest with a parameterless constructor.");
        var manifest = new ApplicationManifestSnapshot((IApplicationManifest)Activator.CreateInstance(provider)!);
        var (deployment, deploymentSource) = ApplicationDeploymentReader.Read(Application.Application.ResolveAppId(), Option.Applications,
            builder.Composition.StartupConfiguration);
        CompiledApplicationModel model;
        try
        {
            model = new CompiledApplicationModel(manifest, deployment, Application.TypeFinder, Application.Application.AutonomousWork);
        }
        catch (InvalidOperationException error)
        {
            throw ApplicationDeploymentReader.ValidationFailure(deploymentSource, error);
        }
        var registered = new HashSet<Type>();
        foreach (var client in manifest.ApiClients)
            if (!registered.Add(client.InterfaceType) || !client.InterfaceType.IsInterface ||
                !client.InterfaceType.IsAssignableFrom(client.ImplementationType) || client.ImplementationType.IsAbstract)
                throw new InvalidOperationException($"Invalid or duplicate application API implementation for '{client.InterfaceType}'.");

        _registrations = ApplicationRegistrationPlan.Create(manifest, model, Application.TypeFinder);
        Application.ProjectApplicationIdentity(deployment.AppId, deployment.AppName);
        Application.SetTypeActivationPolicy(model);
        Option.Freeze();
        _clients = manifest.ApiClients;
        _model = model;
    }

    /// <inheritdoc />
    public void RegisterApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder)
    {
        if (_model is null || _registered)
            throw new InvalidOperationException("Application registration requires one prepared model and may execute only once.");
        _registered = true;
        var registrations = _registrations.Select(plan => (Plan: plan,
            Registration: (ApplicationRegistration)Activator.CreateInstance(plan.RegistrationType)!)).ToArray();
        foreach (var (plan, registration) in registrations)
            builder.Composition.RunRegistration(plan.RegistrationType, () => registration.ConfigurePassive(
                new ApplicationRegistrationContext(builder, hostBuilder, plan.IsActive, Application.Application.AutonomousWork)));
        foreach (var (plan, registration) in registrations.Where(static item => item.Plan.IsActive))
            builder.Composition.RunRegistration(plan.RegistrationType, () => registration.ConfigureActive(
                new ApplicationRegistrationContext(builder, hostBuilder, isActive: true, Application.Application.AutonomousWork)));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleApplicationModelOption> context)
    {
        var model = _model ?? throw new InvalidOperationException("The application model must be prepared before service registration.");
        context.Services.AddSingleton<IApplicationModel>(model);
        context.Services.AddSingleton<Monica.Core.Domains.Abstractions.IDomainCatalogSource, ApplicationDomainCatalogSource>();
        context.Services.AddSingleton(model.Deployment);
        foreach (var client in _clients)
            context.Services.AddScoped(client.InterfaceType, client.ImplementationType);
    }
}

/// <summary>Startup input for an application host. Changes require a host restart.</summary>
public sealed class ModuleApplicationModelOption : ModuleOptions<ModuleApplicationModel>
{
    private IReadOnlyDictionary<string, ApplicationPlacement>? _applications;
    private Type? _manifestType;
    private bool _frozen;

    /// <summary>Supplies the complete AppId map in code. Null selects Deployment:Applications from startup configuration.</summary>
    /// <remarks>Code and configuration cannot both supply this value. Every catalog role needs one owner; selected roles must be implemented locally.</remarks>
    public IReadOnlyDictionary<string, ApplicationPlacement>? Applications
    {
        get => _applications;
        set { EnsureOpen(); _applications = value; }
    }

    /// <summary>Overrides generated-manifest discovery for an explicitly supplied compiled manifest. Null uses the host generator.</summary>
    /// <remarks>The type must implement IApplicationManifest and expose a parameterless constructor; it is created once during composition.</remarks>
    public Type? ManifestType
    {
        get => _manifestType;
        set { EnsureOpen(); _manifestType = value; }
    }

    internal void Freeze() => _frozen = true;

    private void EnsureOpen()
    {
        if (_frozen) throw new InvalidOperationException("Application model inputs are frozen before application registration.");
    }
}
