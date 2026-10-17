using Microsoft.Extensions.DependencyInjection;
using Monica.AI.UI.Localization;
using Monica.AI.UI.Pages;
using Monica.AI.UI.UIKnowledgeBase.State;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Knowledge-base UI module builder extensions.
/// </summary>
public static class ModuleKnowledgeBaseUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures knowledge-base UI components.
        /// </summary>
        /// <param name="action">Optional configuration action.</param>
        /// <returns>The host-bound knowledge-base UI registration.</returns>
        public ModuleRegistration<ModuleKnowledgeBaseUI, ModuleKnowledgeBaseUIOption> AddKnowledgeBaseUI(
            Action<ModuleKnowledgeBaseUIOption>? action = null)
        {
            return builder.AddModule<ModuleKnowledgeBaseUI, ModuleKnowledgeBaseUIOption>(action);
        }
    }
}

/// <summary>
/// UI module for knowledge-base selection and management surfaces.
/// </summary>
/// <remarks>Enables the shell's Markdown services for document previews. RAG UI inherits this prerequisite
/// through its knowledge-base UI dependency.</remarks>
public sealed class ModuleKnowledgeBaseUI : MonicaModule<ModuleKnowledgeBaseUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleKnowledgeBase, ModuleKnowledgeBaseOption>();
        module.Require<ModuleRAG, ModuleRAGOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<AIResource>());
        module.Require<ModuleUI, ModuleUIOption>(options => options.EnableMarkdown = true);
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
        {
            option.ConfigureNavigation(static registry =>
                registry.RegisterLocalizedPage<KnowledgeBaseManagePage, AIResource>(
                    KnowledgeBaseManagePage.PAGE_URL,
                    "Pages:KnowledgeBaseManage:Title",
                    MoIcons.Database,
                    BuiltInNavigationCategoryIds.KnowledgeRetrieval,
                    addToNav: true,
                    navOrder: 3));
        });
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleKnowledgeBaseUIOption> context)
    {
        context.Services.AddScoped<KnowledgeBaseManagePageStateFactory>();
    }
}

/// <summary>
/// Configuration options for the knowledge-base UI module.
/// </summary>
public sealed class ModuleKnowledgeBaseUIOption : ModuleOptions<ModuleKnowledgeBaseUI>
{
}
