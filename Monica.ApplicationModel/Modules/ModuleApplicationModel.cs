using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
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
            Action<ModuleApplicationModelOption> configure) => builder.AddModule<ModuleApplicationModel, ModuleApplicationModelOption>(configure);
    }
}

/// <summary>Owns domain, role, operation, and logical application identity relationships.</summary>
public sealed class ModuleApplicationModel : MonicaModule<ModuleApplicationModelOption>
{
    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleApplicationModelOption> context)
    {
        var providers = Application.TypeFinder.GetAssemblies()
            .Select(assembly => assembly.GetCustomAttribute<GeneratedApplicationManifestAttribute>()?.ProviderType)
            .OfType<Type>().ToArray();
        var provider = Option.ManifestType ?? (providers.Length == 1 ? providers[0] :
            throw new InvalidOperationException($"Expected one generated application manifest, found {providers.Length}. Mark the executable with [assembly: ApplicationHost]."));
        if (!typeof(IApplicationManifest).IsAssignableFrom(provider) || provider.IsAbstract || provider.ContainsGenericParameters)
            throw new InvalidOperationException($"Manifest '{provider}' must be a concrete IApplicationManifest with a parameterless constructor.");
        var manifest = (IApplicationManifest)Activator.CreateInstance(provider)!;
        var deployment = ApplicationDeployment.Create(Application.Application.ResolveAppId(), Option.Deployments);
        var model = new CompiledApplicationModel(manifest, deployment, Application.TypeFinder);
        Application.SetTypeActivationPolicy(model);
        context.Services.AddSingleton<IApplicationModel>(model);
        context.Services.AddSingleton<ITypeActivationPolicy>(model);
        context.Services.AddSingleton(deployment);
        var registered = new HashSet<Type>();
        foreach (var client in manifest.ApiClients)
        {
            if (!registered.Add(client.InterfaceType) || !client.InterfaceType.IsInterface ||
                !client.InterfaceType.IsAssignableFrom(client.ImplementationType) || client.ImplementationType.IsAbstract)
                throw new InvalidOperationException($"Invalid or duplicate application API implementation for '{client.InterfaceType}'.");
            context.Services.AddScoped(client.InterfaceType, client.ImplementationType);
        }
    }
}

/// <summary>Startup input for an application host. Changes require a host restart.</summary>
public sealed class ModuleApplicationModelOption : ModuleOptions<ModuleApplicationModel>
{
    /// <summary>Maps logical AppIds to their exact roles. Every catalog role needs one owner; current-AppId roles must be implemented locally.</summary>
    public IReadOnlyDictionary<string, ApplicationPlacement> Deployments { get; set; } = new Dictionary<string, ApplicationPlacement>();

    /// <summary>Overrides generated-manifest discovery for an explicitly supplied compiled manifest. Null uses the host generator.</summary>
    /// <remarks>The type must implement IApplicationManifest and expose a parameterless constructor; it is created once during composition.</remarks>
    public Type? ManifestType { get; set; }
}
