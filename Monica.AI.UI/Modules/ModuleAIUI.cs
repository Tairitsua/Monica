using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.AI.Chat.Abstractions;
using Monica.AI.UI.Localization;
using Monica.AI.UI.Pages;
using Monica.AI.UI.UIChat.State;
using Monica.AI.UI.UIChat.Support;
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
/// AI UI module registration extension method
/// </summary>
public static class ModuleAIUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configure AI UI module
        /// </summary>
        public ModuleRegistration<ModuleAIUI, ModuleAIUIOption> AddAIUI(
            Action<ModuleAIUIOption>? action = null)
        {
            return builder.AddModule<ModuleAIUI, ModuleAIUIOption>(action);
        }
    }


}

/// <summary>
/// AI UI module implementation
/// Provides an AI chat interface based on Blazor
/// </summary>
public class ModuleAIUI : MonicaModule<ModuleAIUIOption>, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleAI, ModuleAIOption>();
        module.Require<ModuleKnowledgeBase, ModuleKnowledgeBaseOption>();
        module.Require<ModuleSkillSystem, ModuleSkillSystemOption>();
        module.Require<ModuleMcp, ModuleMcpOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<AIResource>());
        module.Require<ModuleUI, ModuleUIOption>(options => options.EnableMarkdown = true);
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
        {
            option.ConfigureNavigation(RegisterNavigation);
        });
    }

    private static void RegisterNavigation(INavigationRegistryBuilder registry)
    {
        registry.RegisterLocalizedPage<ChatPage, AIResource>(
            ChatPage.PAGE_URL,
            "Pages:AIChat:Title",
            MoIcons.Bot,
            BuiltInNavigationCategoryIds.AI,
            addToNav: true,
            navOrder: 1);
        registry.RegisterLocalizedPage<ProviderManagePage, AIResource>(
            ProviderManagePage.PAGE_URL,
            "Pages:AIProviderManage:Title",
            MoIcons.Network,
            BuiltInNavigationCategoryIds.AI,
            addToNav: true,
            navOrder: 2);
        registry.RegisterLocalizedPage<AgentCapabilityManagePage, AIResource>(
            AgentCapabilityManagePage.PAGE_URL,
            "Pages:AISkills:Title",
            MoIcons.Puzzle,
            BuiltInNavigationCategoryIds.AI,
            addToNav: true,
            navOrder: 3);
        registry.RegisterLocalizedPage<McpCapabilityManagePage, AIResource>(
            McpCapabilityManagePage.PAGE_URL,
            "Pages:AIMcp:Title",
            MoIcons.Workflow,
            BuiltInNavigationCategoryIds.AI,
            addToNav: true,
            navOrder: 4);
    }

    public override void ConfigureServices(ModuleContext<ModuleAIUIOption> context)
    {
        // Register UI services
        context.Services.AddScoped<ChatPageStateFactory>();
        context.Services.Replace(ServiceDescriptor.Scoped<IChatUserIdentityAccessor, BlazorChatUserIdentityAccessor>());
        context.Services.AddScoped<ChatSessionWorkspace>();
    }
}

/// <summary>
/// AI UI module configuration options
/// </summary>
public class ModuleAIUIOption : ModuleOptions<ModuleAIUI>
{
    /// <summary>
    /// Enable Markdown rendering
    /// </summary>
    public bool EnableMarkdown { get; set; } = true;

    /// <summary>
    /// Show provider selector
    /// </summary>
    public bool ShowProviderSelector { get; set; } = true;

    /// <summary>
    /// Show session list
    /// </summary>
    public bool ShowSessionList { get; set; } = true;

    /// <summary>
    /// Default system prompt (overrides backend setting)
    /// </summary>
    public string? DefaultSystemPrompt { get; set; }

    /// <summary>
    /// Optional total operation timeout in milliseconds. The default is zero (no UI deadline),
    /// allowing long reasoning and multi-tool runs. Provider request timeouts still apply independently.
    /// </summary>
    public int RequestTimeoutMs { get; set; }

    /// <summary>
    /// Enable auto-scroll to bottom on new messages
    /// </summary>
    public bool EnableAutoScroll { get; set; } = true;

    /// <summary>
    /// Default knowledge base IDs to pre-select in the chat UI.
    /// </summary>
    public List<string> DefaultKnowledgeBaseIds { get; set; } = [];

    /// <summary>
    /// Whether to show the knowledge base selector in the chat UI.
    /// </summary>
    public bool ShowKnowledgeBaseSelector { get; set; } = true;
}
