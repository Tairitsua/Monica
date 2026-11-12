using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Locker.Leases.Diagnostics;
using Monica.Locker.UI.Localization;
using Monica.Locker.UI.Pages;
using Monica.Locker.UI.State;
using Monica.UI.Icons;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;

namespace Monica.Modules;

/// <summary>Registers the authorized lease workbench and the provider-contract comparison.</summary>
public static class ModuleLockerUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Adds /leases and /leases/locks. Configure authority bindings with AddLeaseDiagnostics.</summary>
        /// <remarks>No lease store or critical-section lock provider is selected implicitly.</remarks>
        public ModuleRegistration<ModuleLockerUI, ModuleLockerUIOption> AddLockerUI(
            Action<ModuleLockerUIOption>? configure = null) => builder.AddModule<ModuleLockerUI, ModuleLockerUIOption>(configure);
    }
}

/// <summary>Composes the optional Blazor lease and lock workbenches.</summary>
public sealed class ModuleLockerUI : MonicaModule<ModuleLockerUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleLeaseDiagnostics, ModuleLeaseDiagnosticsOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(options => options.AddResource<LockerResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(options => options.ConfigureNavigation(registry =>
        {
            var category = registry.RegisterLocalizedCategory<LockerResource>("Monica.Locker", "Navigation:Category", 15,
                parentId: BuiltInNavigationCategoryIds.Monitor, icon: MoIcons.Key);
            registry.RegisterLocalizedPage<UILeasesPage, LockerResource>(UILeasesPage.PAGE_URL, "Navigation:Leases",
                MoIcons.Network, category, addToNav: true, navOrder: 10,
                accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleLockerUIOption>));
            registry.RegisterLocalizedPage<UILocksPage, LockerResource>(UILocksPage.PAGE_URL, "Navigation:Locks",
                MoIcons.Lock, category, addToNav: true, navOrder: 20,
                accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleLockerUIOption>));
        }));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleLockerUIOption> context)
    {
        context.Services.AddScoped<OperationalPageAccessPolicy<ModuleLockerUIOption>>();
        context.Services.AddScoped<ILeaseDiagnosticsAccess, LeaseUiAccess>();
        context.Services.AddScoped<LeaseWorkbenchState>();
    }
}

/// <summary>Configures lease workbench access and its initial refresh preference.</summary>
public sealed class ModuleLockerUIOption : ModuleOptions<ModuleLockerUI>, IOperationalPageAccessOptions
{
    /// <summary>Overrides the shell operational policy; otherwise inherits it. Access is denied outside Development without a policy.</summary>
    public string? AuthorizationPolicyOverride { get; set; }
    /// <summary>Initial refresh interval in seconds; supported values are 0 (Off), 15, 30 and 60. Defaults to 30.</summary>
    /// <remarks>Each circuit remembers its selection across routes. Hidden tabs pause and inventory requests share the host cache.</remarks>
    public int AutoRefreshSeconds { get; set; } = 30;
}

internal sealed class LeaseUiAccess(OperationalPageAccessEvaluator evaluator, IOptions<ModuleLockerUIOption> options)
    : ILeaseDiagnosticsAccess
{
    public Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default) =>
        evaluator.IsAuthorizedAsync(options.Value.AuthorizationPolicyOverride, cancellationToken);
}
