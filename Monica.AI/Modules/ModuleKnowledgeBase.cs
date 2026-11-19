using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Monica.AI.KnowledgeBase.Abstractions;
using Monica.AI.KnowledgeBase.Facades;
using Monica.AI.KnowledgeBase.Providers;
using Monica.AI.KnowledgeBase.Services;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Markdown.Abstractions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Extension methods for configuring the knowledge-base module.
/// </summary>
public static class ModuleKnowledgeBaseBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures knowledge-base inventory and lookup services.
        /// </summary>
        /// <param name="action">Optional configuration action.</param>
        /// <returns>The knowledge-base module registration.</returns>
        public ModuleRegistration<ModuleKnowledgeBase, ModuleKnowledgeBaseOption> AddKnowledgeBase(Action<ModuleKnowledgeBaseOption>? action = null)
        {
            return builder.AddModule<ModuleKnowledgeBase, ModuleKnowledgeBaseOption>(action);
        }
    }
}

/// <summary>
/// Knowledge-base inventory and lookup module.
/// </summary>
public sealed class ModuleKnowledgeBase : MonicaModule<ModuleKnowledgeBaseOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleSkillSystem>();
        module.Require<ModuleMarkdown>();
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleKnowledgeBaseOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton<IDocumentIndexStateStore, FileDocumentIndexStateStore>();
        services.TryAddSingleton<IKnowledgeDocumentSourceStore, FileKnowledgeDocumentSourceStore>();
        services.TryAddSingleton<IKnowledgeBaseStore, DocumentIndexStateKnowledgeBaseStore>();
        services.TryAddSingleton<KnowledgeBaseService>();
        services.TryAddSingleton<KnowledgeDocumentService>();
        services.TryAddSingleton<IKnowledgeDocumentQueryService, KnowledgeDocumentQueryService>();
        services.AddScoped(sp => new KnowledgeBaseFacade(
            sp.GetRequiredService<KnowledgeBaseService>(),
            sp.GetRequiredService<ILogger<KnowledgeBaseFacade>>()));
        services.AddScoped(sp => new KnowledgeDocumentFacade(
            sp.GetRequiredService<KnowledgeDocumentService>(),
            sp.GetRequiredService<IMarkdownDocumentCatalog>(),
            sp.GetRequiredService<ILogger<KnowledgeDocumentFacade>>()));
    }
}

/// <summary>
/// Knowledge-base module options.
/// </summary>
public sealed class ModuleKnowledgeBaseOption : ModuleOptions<ModuleKnowledgeBase>
{
    /// <summary>
    /// Relative file path for the unified knowledge-base and document index state store.
    /// Resolved relative to the application's running directory.
    /// </summary>
    public string DocumentIndexStateStoreFilePath { get; set; } = "monica_data/rag/document_index_state.json";

    /// <summary>
    /// Relative root path for source document content storage.
    /// </summary>
    public string UploadedDocumentSourceRootPath { get; set; } = "monica_data/rag/document_sources";
}

/// <summary>
/// Registration extensions for the knowledge-base module.
/// </summary>
public static class ModuleKnowledgeBaseRegistrationExtensions
{
    /// <summary>
    /// Uses a custom unified state store implementation.
    /// </summary>
    /// <typeparam name="TStore">Custom document index state store type.</typeparam>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleKnowledgeBase, ModuleKnowledgeBaseOption> UseDocumentIndexStateStore<TStore>(this ModuleRegistration<ModuleKnowledgeBase, ModuleKnowledgeBaseOption> module)
        where TStore : class, IDocumentIndexStateStore
    {
        module.ConfigureServices(ctx =>
        {
            ctx.Services.AddSingleton<IDocumentIndexStateStore, TStore>();
        });
        return module;
    }

    /// <summary>
    /// Uses the built-in file-based unified state store.
    /// </summary>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleKnowledgeBase, ModuleKnowledgeBaseOption> UseDocumentIndexStateFileProvider(this ModuleRegistration<ModuleKnowledgeBase, ModuleKnowledgeBaseOption> module)
    {
        module.ConfigureServices(ctx =>
        {
            ctx.Services.AddSingleton<IDocumentIndexStateStore, FileDocumentIndexStateStore>();
        });
        return module;
    }

    /// <summary>
    /// Uses a custom source content store implementation.
    /// </summary>
    /// <typeparam name="TStore">Custom source document store type.</typeparam>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleKnowledgeBase, ModuleKnowledgeBaseOption> UseKnowledgeDocumentSourceStore<TStore>(this ModuleRegistration<ModuleKnowledgeBase, ModuleKnowledgeBaseOption> module)
        where TStore : class, IKnowledgeDocumentSourceStore
    {
        module.ConfigureServices(ctx =>
        {
            ctx.Services.AddSingleton<IKnowledgeDocumentSourceStore, TStore>();
        });
        return module;
    }

    /// <summary>
    /// Uses the built-in file-based source content store.
    /// </summary>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleKnowledgeBase, ModuleKnowledgeBaseOption> UseKnowledgeDocumentSourceFileProvider(this ModuleRegistration<ModuleKnowledgeBase, ModuleKnowledgeBaseOption> module)
    {
        module.ConfigureServices(ctx =>
        {
            ctx.Services.AddSingleton<IKnowledgeDocumentSourceStore, FileKnowledgeDocumentSourceStore>();
        });
        return module;
    }

}
