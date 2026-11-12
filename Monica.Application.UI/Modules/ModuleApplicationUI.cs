using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Monica.Application.Abstractions;
using Monica.Application.UI.Services;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;

namespace Monica.Modules;

/// <summary>Composes the standard server-side application UI profile.</summary>
public static class ModuleApplicationUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Adds UI hosting and per-operation application dispatch without the default navigation shell.</summary>
        /// <remarks>Uses the host's AuthenticationStateProvider. Configure authentication, page assemblies and the product layout in the host.</remarks>
        public ModuleRegistration<ModuleApplicationUI, ModuleApplicationUIOption> AddApplicationUI(
            Action<ModuleApplicationUIOption>? configure = null)
            => builder.AddModule<ModuleApplicationUI, ModuleApplicationUIOption>(configure);
    }
}

/// <summary>Bridges a server UI circuit to the shared application execution pipeline.</summary>
public class ModuleApplicationUI : MonicaModule<ModuleApplicationUIOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleApplication, ModuleApplicationOption>();
        module.Require<ModuleUIHost, ModuleUIHostOption>();
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleApplicationUIOption> context)
    {
        context.Services.AddCascadingAuthenticationState();
        context.Services.AddScoped<ApplicationCircuitState>();
        context.Services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, ApplicationCircuitHandler>());
        context.Services.Replace(ServiceDescriptor.Scoped<IApplicationCallerContext, ApplicationUICallerContext>());
    }
}

/// <summary>Configures server-side application UI integration.</summary>
public class ModuleApplicationUIOption : ModuleOptions<ModuleApplicationUI>;
