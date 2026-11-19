using Microsoft.Extensions.DependencyInjection;
using Monica.AI.UI.Localization;
using Monica.AI.UI.Pages;
using Monica.AI.UI.UIRAG.State;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// RAG UI module builder extensions.
/// </summary>
public static class ModuleRAGUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the RAG UI module.
        /// </summary>
        public ModuleRegistration<ModuleRAGUI, ModuleRAGUIOption> AddRAGUI(
            Action<ModuleRAGUIOption>? action = null)
        {
            return builder.AddModule<ModuleRAGUI, ModuleRAGUIOption>(action);
        }
    }

}

/// <summary>
/// RAG UI module.
/// Provides Blazor-based RAG debug and management interface.
/// </summary>
public class ModuleRAGUI : MonicaModule<ModuleRAGUIOption>, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleRAG>();
        module.Require<ModuleKnowledgeBaseUI>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<AIResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(
            static option => option.ConfigureNavigation(RegisterNavigation));
    }

    private static void RegisterNavigation(INavigationRegistryBuilder registry)
    {
        registry.RegisterLocalizedPage<RAGManagePage, AIResource>(
            RAGManagePage.PAGE_URL,
            "Pages:RAGManage:Title",
            MoIcons.ListVideo,
            BuiltInNavigationCategoryIds.KnowledgeRetrieval,
            addToNav: true,
            navOrder: 4);
        registry.RegisterLocalizedPage<RAGDebugPage, AIResource>(
            RAGDebugPage.PAGE_URL,
            "Pages:RAGDebug:Title",
            MoIcons.TextSearch,
            BuiltInNavigationCategoryIds.KnowledgeRetrieval,
            addToNav: true,
            navOrder: 5);
        registry.RegisterLocalizedPage<RAGChunkersPage, AIResource>(
            RAGChunkersPage.PAGE_URL,
            "Pages:RAGChunkers:Title",
            MoIcons.Workflow,
            BuiltInNavigationCategoryIds.KnowledgeRetrieval,
            addToNav: true,
            navOrder: 6);
    }

    public override void ConfigureServices(ModuleContext<ModuleRAGUIOption> context)
    {
        context.Services.AddScoped<RAGManagePageStateFactory>();
    }
}

/// <summary>
/// RAG UI module configuration options.
/// </summary>
public class ModuleRAGUIOption : ModuleOptions<ModuleRAGUI>
{
}
