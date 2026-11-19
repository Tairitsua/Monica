using System.Net;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using CommunityToolkit.VectorData.InMemory;
using CommunityToolkit.VectorData.Qdrant;
using Monica.AI.Abstractions;
using Monica.AI.KnowledgeBase.Abstractions;
using Monica.AI.Models;
using Monica.AI.Providers;
using Monica.AI.RAG.Abstractions;
using Monica.AI.RAG.Facades;
using Monica.AI.RAG.Models;
using Monica.AI.RAG.Providers;
using Monica.AI.Services;
using Monica.AI.RAG.Services;
using Monica.AI.RAG.Services.Support;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// RAG module builder extensions.
/// </summary>
public static class ModuleRAGBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the RAG module.
        /// </summary>
        public ModuleRegistration<ModuleRAG, ModuleRAGOption> AddRAG(Action<ModuleRAGOption>? action = null)
        {
            return builder.AddModule<ModuleRAG, ModuleRAGOption>(action);
        }
    }
}

/// <summary>
/// RAG (Retrieval-Augmented Generation) module.
/// </summary>
public class ModuleRAG : MonicaModule<ModuleRAGOption>
{
    internal const string VECTOR_STORE_FEATURE = "vector-store";

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleKnowledgeBase>();
        module.Require<ModuleMarkdown>();
        module.RequireFeature(VECTOR_STORE_FEATURE);
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleRAGOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton<ITokenCountProvider, EstimatedUtf8TokenCountProvider>();
        services.AddSingleton<RAGIndexingActivity>();
        services.AddSingleton<RAGDocumentService>();
        services.AddSingleton<RAGDocumentIndexingService>();
        services.AddSingleton<RAGVectorStoreService>();
        services.AddSingleton<RAGSearchService>();
        services.AddSingleton<RAGChunkingService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IKnowledgeBaseLifecycleHandler, RAGKnowledgeBaseLifecycleHandler>());
        services.AddSingleton<RAGEmbeddingBindingResolver>();
        services.AddSingleton<RAGVectorCollectionCoordinator>();
        services.AddSingleton<RAGIndexStateCoordinator>();
        services.AddSingleton<ChunkerRegistry>();
        services.TryAddSingleton<IChunkerRoutingStore, FileChunkerRoutingStore>();
        services.AddScoped<MarkdownDocumentResolver>();
        services.AddScoped<ChunkViewCoordinator>();
        services.AddSingleton<RAGBatchIndexOperationRegistry>();
        services.AddScoped<BatchIndexCoordinator>();
        services.AddScoped(sp => new RAGSearchFacade(
            sp.GetRequiredService<RAGSearchService>(),
            sp.GetRequiredService<ILogger<RAGSearchFacade>>()));
        services.AddScoped(sp => new RAGIndexingFacade(
            sp.GetRequiredService<RAGDocumentService>(),
            sp.GetRequiredService<BatchIndexCoordinator>(),
            sp.GetRequiredService<ChunkViewCoordinator>(),
            sp.GetRequiredService<ILogger<RAGIndexingFacade>>()));
        services.AddScoped(sp => new RAGVectorStoreFacade(
            sp.GetRequiredService<RAGVectorStoreService>(),
            sp.GetRequiredService<ILogger<RAGVectorStoreFacade>>()));
        services.AddScoped<EmbeddingModelFacade>();
        services.AddScoped(sp => new ChunkerFacade(
            sp.GetRequiredService<RAGChunkingService>(),
            sp.GetRequiredService<ILogger<ChunkerFacade>>()));

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDocumentChunker, ProductionMarkdownDocumentChunker>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDocumentChunker, SimpleMarkdownDocumentChunker>());
    }
}

/// <summary>
/// RAG module options.
/// </summary>
public class ModuleRAGOption : ModuleOptions<ModuleRAG>
{
    /// <summary>
    /// Gets the Qdrant provider settings used when Qdrant is selected as the vector store.
    /// </summary>
    public ModuleRAGQdrantOption Qdrant { get; } = new();

    /// <summary>
    /// Gets or sets the prefix applied to vector collection names owned by Monica RAG.
    /// The default is <c>monica_rag_</c>; change it when multiple applications share one store.
    /// </summary>
    public string CollectionNamePrefix { get; set; } = "monica_rag_";

    /// <summary>
    /// Gets or sets the default maximum number of semantic matches returned by retrieval.
    /// The default is <c>5</c> and callers may override it per search.
    /// </summary>
    public int DefaultTopK { get; set; } = 5;

    /// <summary>
    /// Relative file path for extension-to-chunker routing configuration.
    /// </summary>
    public string ChunkerRoutingStoreFilePath { get; set; } = "monica_data/rag/chunker_routing.json";

    /// <summary>
    /// Target chunk size for the production markdown chunker.
    /// </summary>
    public int ProductionChunkerTargetChars { get; set; } = 1200;

    /// <summary>
    /// Maximum chunk size for the production markdown chunker.
    /// </summary>
    public int ProductionChunkerMaxChars { get; set; } = 1800;

    /// <summary>
    /// Minimum chunk size for the production markdown chunker.
    /// </summary>
    public int ProductionChunkerMinChars { get; set; } = 200;

    /// <summary>
    /// Overlap size between adjacent chunks for the production markdown chunker.
    /// </summary>
    public int ProductionChunkerOverlapChars { get; set; } = 120;

    /// <summary>
    /// Options for the TextSearchProvider used in agent integration (Phase 3).
    /// Controls search behavior (BeforeAIInvoke vs OnDemandFunctionCalling),
    /// result formatting, and recent message memory.
    /// </summary>
    public TextSearchProviderOptions? SearchProviderOptions { get; set; }
}

/// <summary>
/// Qdrant-specific settings for the RAG vector store provider.
/// </summary>
public class ModuleRAGQdrantOption
{
    /// <summary>
    /// Qdrant host name.
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Qdrant gRPC port.
    /// </summary>
    public int Port { get; set; } = 6334;

    /// <summary>
    /// Whether to use HTTPS for the Qdrant connection.
    /// </summary>
    public bool Https { get; set; }

    /// <summary>
    /// Optional Qdrant API key.
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// Registration extensions for configuring the RAG module.
/// </summary>
public static class ModuleRAGRegistrationExtensions
{
    /// <summary>
    /// Uses a custom unified state store implementation.
    /// </summary>
    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> UseDocumentIndexStateStore<TStore>(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module)
        where TStore : class, IDocumentIndexStateStore
    {
        module.Require<ModuleKnowledgeBase, ModuleKnowledgeBaseOption>()
            .UseDocumentIndexStateStore<TStore>();
        return module;
    }

    /// <summary>
    /// Uses the file-based unified state store.
    /// </summary>
    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> UseDocumentIndexStateFileProvider(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module)
    {
        module.Require<ModuleKnowledgeBase, ModuleKnowledgeBaseOption>()
            .UseDocumentIndexStateFileProvider();
        return module;
    }

    /// <summary>
    /// Uses a custom source content store implementation.
    /// </summary>
    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> UseKnowledgeDocumentSourceStore<TStore>(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module)
        where TStore : class, IKnowledgeDocumentSourceStore
    {
        module.Require<ModuleKnowledgeBase, ModuleKnowledgeBaseOption>()
            .UseKnowledgeDocumentSourceStore<TStore>();
        return module;
    }

    /// <summary>
    /// Uses file-based source content store.
    /// </summary>
    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> UseKnowledgeDocumentSourceFileProvider(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module)
    {
        module.Require<ModuleKnowledgeBase, ModuleKnowledgeBaseOption>()
            .UseKnowledgeDocumentSourceFileProvider();
        return module;
    }

    /// <summary>
    /// Uses the in-memory vector store (for development/testing).
    /// Embedding model selection is resolved per knowledge base by <see cref="RAGEmbeddingBindingResolver"/>.
    /// </summary>
    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> UseVectorStoreInMemoryProvider(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module)
    {
        module.SatisfyFeature(ModuleRAG.VECTOR_STORE_FEATURE);
        module.ConfigureServices(ctx =>
        {
            ctx.Services.AddSingleton<VectorStore>(_ => new InMemoryVectorStore());
            ctx.Services.AddSingleton(new RAGVectorStoreRegistrationInfo
            {
                ProviderKind = "InMemory",
                ProviderDisplayName = "In-Memory"
            });
        });
        return module;
    }

    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> UseVectorStoreProvider<TVectorStore>(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module)
        where TVectorStore : VectorStore
    {
        module.SatisfyFeature(ModuleRAG.VECTOR_STORE_FEATURE);
        module.ConfigureServices(ctx =>
        {
            ctx.Services.AddSingleton<VectorStore, TVectorStore>();
            ctx.Services.AddSingleton(new RAGVectorStoreRegistrationInfo
            {
                ProviderKind = "Custom",
                ProviderDisplayName = typeof(TVectorStore).Name
            });
        });
        return module;
    }

    /// <summary>
    /// Uses Qdrant as the vector store provider.
    /// </summary>
    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> UseVectorStoreQdrantProvider(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module,
        Action<ModuleRAGQdrantOption>? action = null)
    {
        module.SatisfyFeature(ModuleRAG.VECTOR_STORE_FEATURE);
        module.Configure(options => action?.Invoke(options.Qdrant));
        module.ConfigureServices(ctx =>
        {
            var option = ctx.Options.Qdrant;
            ctx.Services.AddQdrantVectorStore(
                NormalizeQdrantHost(option.Host, option.Https),
                option.Port,
                option.Https,
                option.ApiKey ?? string.Empty,
                new QdrantVectorStoreOptions());
            ctx.Services.AddSingleton(CreateQdrantVectorStoreRegistrationInfo(option));
        });

        return module;
    }

    private static RAGVectorStoreRegistrationInfo CreateQdrantVectorStoreRegistrationInfo(ModuleRAGQdrantOption option)
    {
        return new RAGVectorStoreRegistrationInfo
        {
            ProviderKind = "Qdrant",
            ProviderDisplayName = "Qdrant",
            ConfigurationEntries =
            [
                new VectorStoreConfigurationEntry
                {
                    Key = nameof(ModuleRAGQdrantOption.Host),
                    Value = NormalizeQdrantHost(option.Host, option.Https)
                },
                new VectorStoreConfigurationEntry
                {
                    Key = nameof(ModuleRAGQdrantOption.Port),
                    Value = option.Port.ToString()
                },
                new VectorStoreConfigurationEntry
                {
                    Key = nameof(ModuleRAGQdrantOption.Https),
                    Value = option.Https.ToString()
                },
                new VectorStoreConfigurationEntry
                {
                    Key = nameof(ModuleRAGQdrantOption.ApiKey),
                    Value = string.IsNullOrWhiteSpace(option.ApiKey) ? "Not configured" : "Configured",
                    IsSensitive = true
                }
            ]
        };
    }

    private static string NormalizeQdrantHost(string host, bool https)
        // Qdrant is accessed through Qdrant.Client, which uses gRPC for the 6334 endpoint.
        // In this environment the app runs as a Windows .NET process while Qdrant is published
        // from Docker Compose on the host. Using "localhost" can make the gRPC client probe the
        // IPv6 loopback (::1) path first. We reproduced that ::1:6334 waits about 21 seconds and
        // then fails, while 127.0.0.1:6334 succeeds in a few milliseconds. That makes the first
        // CollectionExistsAsync on a fresh process appear extremely slow even though Qdrant itself
        // is healthy. Normalize "localhost" to the IPv4 loopback for non-HTTPS local connections
        // so the client skips the bad ::1 path and reaches the published gRPC port immediately.
        => !https && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback.ToString()
            : host;

    /// <summary>
    /// Uses a custom chunker routing store implementation.
    /// </summary>
    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> UseChunkerRoutingStore<TStore>(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module)
        where TStore : class, IChunkerRoutingStore
    {
        module.ConfigureServices(ctx =>
        {
            ctx.Services.AddSingleton<IChunkerRoutingStore, TStore>();
        });
        return module;
    }

    /// <summary>
    /// Uses file-based extension routing store.
    /// </summary>
    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> UseChunkerRoutingFileProvider(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module)
    {
        module.ConfigureServices(ctx =>
        {
            ctx.Services.AddSingleton<IChunkerRoutingStore, FileChunkerRoutingStore>();
        });
        return module;
    }

    /// <summary>
    /// Uses fake embeddings for testing and development through the unified AI provider pipeline.
    /// </summary>
    /// <param name="module">The RAG module registration to configure.</param>
    /// <param name="dimensions">Embedding dimensions.</param>
    /// <param name="providerId">Optional provider ID used for fake embedding provider. Defaults to provider type.</param>
    /// <param name="modelName">Optional model name. If null, uses `Fake-Embeddings-{dimensions}d`.</param>
    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> AddFakeEmbeddingsModel(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module,
        int dimensions = 384,
        string? providerId = null,
        string? modelName = null)
    {
        var resolvedProviderId = providerId ?? nameof(EAIProviderType.Fake);
        var resolvedModelName = string.IsNullOrWhiteSpace(modelName)
            ? $"Fake-Embeddings-{dimensions}d"
            : modelName.Trim();

        module.Require<ModuleAI, ModuleAIOption>()
            .AddModel(new EmbeddingModelInfo
            {
                ModelName = resolvedModelName,
                Description = "Fake embedding model for development and testing.",
                Dimensions = dimensions
            })
            .AddFakeProvider(options =>
            {
                options.ApiKey = "fake";
                options.DefaultDimensions = dimensions;
                options.SupportedModels = [resolvedModelName];
            }, resolvedProviderId);

        return module;
    }

    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> AddChunker<TChunker>(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module)
        where TChunker : class, IDocumentChunker
    {
        module.ConfigureServices(ctx =>
        {
            ctx.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IDocumentChunker, TChunker>());
        });
        return module;
    }

    /// <summary>
    /// Registers one built-in chunker.
    /// </summary>
    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> AddBuiltInChunker(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module, RAGBuiltInChunkerType chunkerType)
    {
        return chunkerType switch
        {
            RAGBuiltInChunkerType.ProductionMarkdown => module.AddChunker<ProductionMarkdownDocumentChunker>(),
            RAGBuiltInChunkerType.SimpleMarkdown => module.AddChunker<SimpleMarkdownDocumentChunker>(),
            _ => module
        };
    }

    /// <summary>
    /// Registers selected built-in chunkers.
    /// </summary>
    public static ModuleRegistration<ModuleRAG, ModuleRAGOption> AddBuiltInChunkers(this ModuleRegistration<ModuleRAG, ModuleRAGOption> module, params RAGBuiltInChunkerType[] chunkerTypes)
    {
        if (chunkerTypes is null || chunkerTypes.Length == 0)
        {
            return module.AddBuiltInChunker(RAGBuiltInChunkerType.ProductionMarkdown)
                .AddBuiltInChunker(RAGBuiltInChunkerType.SimpleMarkdown);
        }

        foreach (var chunkerType in chunkerTypes.Distinct())
        {
            module.AddBuiltInChunker(chunkerType);
        }

        return module;
    }

}

/// <summary>
/// Built-in chunker kinds provided by ModuleRAG.
/// </summary>
public enum RAGBuiltInChunkerType
{
    ProductionMarkdown = 1,
    SimpleMarkdown = 2
}
