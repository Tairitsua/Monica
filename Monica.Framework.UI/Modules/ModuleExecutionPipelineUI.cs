using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.Pages;
using Monica.Framework.UI.UIExecutionPipeline.State;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Provides host-bound registration for the execution-pipeline catalog UI.
/// </summary>
public static class ModuleExecutionPipelineUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the localized execution-pipeline catalog page for the current Monica host.
        /// </summary>
        /// <param name="configure">Optional page configuration.</param>
        /// <returns>The host-bound module registration.</returns>
        public ModuleRegistration<ModuleExecutionPipelineUI, ModuleExecutionPipelineUIOption> AddExecutionPipelineUI(
            Action<ModuleExecutionPipelineUIOption>? configure = null)
        {
            return builder.AddModule<ModuleExecutionPipelineUI, ModuleExecutionPipelineUIOption>(configure);
        }
    }
}

/// <summary>
/// Registers the execution-pipeline runtime catalog presentation layer.
/// </summary>
public sealed class ModuleExecutionPipelineUI : MonicaModule<ModuleExecutionPipelineUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<ExecutionPipelineResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<ExecutionPipelineResource>(
                    "Monica.Framework.UI.ExecutionPipeline", "Navigation:Category", 20,
                    parentId: BuiltInNavigationCategoryIds.Infrastructure,
                    icon: MoIcons.Workflow);

                registry.RegisterLocalizedPage<UIExecutionPipelinePage, ExecutionPipelineResource>(
                    UIExecutionPipelinePage.PAGE_URL,
                    "Pages:ExecutionPipeline:Title",
                    MoIcons.Workflow,
                    category,
                    addToNav: true,
                    navOrder: 20);
            }));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleExecutionPipelineUIOption> context)
    {
        context.Services.AddScoped<ExecutionPipelinePageState>();
    }
}

/// <summary>
/// Configures the execution-pipeline catalog UI module.
/// </summary>
public sealed class ModuleExecutionPipelineUIOption : ModuleOptions<ModuleExecutionPipelineUI>
{
}
