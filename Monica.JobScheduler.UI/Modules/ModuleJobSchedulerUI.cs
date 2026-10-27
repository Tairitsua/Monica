using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.Pages;
using Monica.JobScheduler.UI.UIJobScheduler.Executions.State;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleJobSchedulerUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Adds the localized JobScheduler operational workspace.
        /// </summary>
        public ModuleRegistration<ModuleJobSchedulerUI, ModuleJobSchedulerUIOption> AddJobSchedulerUI(
            Action<ModuleJobSchedulerUIOption>? action = null)
        {
            return builder.AddModule<ModuleJobSchedulerUI, ModuleJobSchedulerUIOption>(action);
        }
    }
}

/// <summary>
/// Registers the JobScheduler definition, policy, and durable-execution user interface.
/// </summary>
public sealed class ModuleJobSchedulerUI : MonicaModule<ModuleJobSchedulerUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleJobScheduler, ModuleJobSchedulerOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<JobSchedulerResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(
            static option => option.ConfigureNavigation(RegisterNavigation));
    }

    private static void RegisterNavigation(INavigationRegistryBuilder registry)
    {
        registry.RegisterLocalizedPage<SchedulerOverviewPage, JobSchedulerResource>(
            SchedulerOverviewPage.PAGE_URL,
            "Pages:Overview:Title",
            MoIcons.Dashboard,
            BuiltInNavigationCategoryIds.TaskScheduling,
            addToNav: true,
            navOrder: 99,
            accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleJobSchedulerUIOption>));
        registry.RegisterLocalizedPage<JobCatalogPage, JobSchedulerResource>(
            JobCatalogPage.PAGE_URL,
            "Pages:Catalog:Title",
            MoIcons.Package,
            BuiltInNavigationCategoryIds.TaskScheduling,
            addToNav: true,
            navOrder: 100,
            accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleJobSchedulerUIOption>));
        registry.RegisterLocalizedPage<JobExecutionsPage, JobSchedulerResource>(
            JobExecutionsPage.PAGE_URL,
            "Pages:Executions:Title",
            MoIcons.Receipt,
            BuiltInNavigationCategoryIds.TaskScheduling,
            addToNav: true,
            navOrder: 101,
            accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleJobSchedulerUIOption>));
        registry.RegisterLocalizedPage<SchedulerStatisticsPage, JobSchedulerResource>(
            SchedulerStatisticsPage.PAGE_URL,
            "Pages:Statistics:Title",
            MoIcons.ChartTrend,
            BuiltInNavigationCategoryIds.TaskScheduling,
            addToNav: true,
            navOrder: 102,
            accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleJobSchedulerUIOption>));
        registry.RegisterLocalizedPage<JobDefinitionDetailPage, JobSchedulerResource>(
            JobDefinitionDetailPage.PAGE_URL,
            "Pages:JobDetail:Title",
            MoIcons.History,
            BuiltInNavigationCategoryIds.TaskScheduling,
            addToNav: false,
            navOrder: 103,
            accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleJobSchedulerUIOption>));
        registry.RegisterLocalizedPage<SchedulerRuntimePage, JobSchedulerResource>(
            SchedulerRuntimePage.PAGE_URL,
            "Pages:Runtime:Title",
            MoIcons.HeartPulse,
            BuiltInNavigationCategoryIds.TaskScheduling,
            addToNav: true,
            navOrder: 104,
            accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleJobSchedulerUIOption>));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleJobSchedulerUIOption> context)
    {
        context.Services.TryAddScoped<OperationalPageAccessPolicy<ModuleJobSchedulerUIOption>>();
        context.Services.TryAddScoped<IJobSchedulerUiAccess, JobSchedulerUiAccess>();
        context.Services.TryAddScoped<SchedulerTimePresentation>();
        context.Services.TryAddScoped<SchedulerOverviewPageStateFactory>();
        context.Services.TryAddScoped<JobCatalogPageStateFactory>();
        context.Services.TryAddScoped<JobDefinitionDetailPageStateFactory>();
        context.Services.TryAddScoped<JobExecutionsStateFactory>();
        context.Services.TryAddScoped<SchedulerStatisticsPageStateFactory>();
        context.Services.TryAddScoped<SchedulerRuntimePageStateFactory>();
    }
}

/// <summary>
/// Configures the JobScheduler operational workspace.
/// </summary>
public sealed class ModuleJobSchedulerUIOption : ModuleOptions<ModuleJobSchedulerUI>, IOperationalPageAccessOptions
{
    /// <summary>
    /// Gets or sets the host authorization policy that overrides the shell-wide operational policy for every
    /// JobScheduler page and mutation. A missing value inherits the shell policy. Outside Development, a missing
    /// effective policy denies access.
    /// </summary>
    public string? AuthorizationPolicyOverride { get; set; }

    /// <summary>
    /// Gets or sets the overview and active execution inspector refresh interval. The default is five seconds.
    /// A non-positive value disables automatic refresh. Inspectors stop polling when the execution is terminal.
    /// </summary>
    public TimeSpan AutoRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the default catalog and execution page size. The default is 20.
    /// </summary>
    public int DefaultPageSize { get; set; } = 20;
}
