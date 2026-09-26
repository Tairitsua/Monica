using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.AI.AgentCapabilities.Abstractions;
using Monica.AI.AgentCapabilities.Services;
using Monica.AI.Abstractions;
using Monica.AI.Chat.Abstractions;
using Monica.AI.Chat.Facades;
using Monica.AI.Chat.Providers;
using Monica.AI.Storage.Providers;
using Monica.AI.Configuration;
using Monica.AI.Configuration.Abstractions;
using Monica.AI.Configuration.Facades;
using Monica.AI.Configuration.Providers;
using Monica.AI.Configuration.Services;
using Monica.AI.Facades;
using Monica.AI.Models;
using Monica.AI.Providers;
using Monica.AI.Providers.Anthropic;
using Monica.AI.Providers.Fake;
using Monica.AI.Providers.OpenAI;
using Monica.AI.Services;
using Monica.AI.Services.Support;
using Monica.Core;
using Monica.Core.Extensions;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Extension methods for configuring the AI module builder.
/// </summary>
public static class ModuleAIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the AI module.
        /// </summary>
        /// <param name="action">The module configuration action.</param>
        /// <returns>An AI module configuration builder.</returns>
        public ModuleRegistration<ModuleAI, ModuleAIOption> AddAI(Action<ModuleAIOption>? action = null)
        {
            return builder.AddModule<ModuleAI, ModuleAIOption>(action);
        }
    }
}

/// <summary>
/// AI module.
/// </summary>
public class ModuleAI : MonicaModule<ModuleAIOption>
{
    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleAIOption> context)
    {
        var services = context.Services;
        services.AddDataProtection();
        services.TryAddSingleton<IAIConfigurationStore, FileAIConfigurationStore>();
        services.TryAddSingleton<AIConfigurationService>();
        services.AddScoped(sp => new AIConfigurationFacade(
            sp.GetRequiredService<AIConfigurationService>(),
            sp.GetRequiredService<IAIProviderFactory>(),
            sp.GetRequiredService<AIModelCatalog>()));
        // Register model directory
        services.AddSingleton(sp =>
        {
            var catalog = new AIModelCatalog();
            catalog.AddReservedModels(ModuleAIOption.GetReservedModels());

            foreach (var model in Option.ModelRegistrations)
            {
                catalog.AddModel(model);
            }

            return catalog;
        });

        // Register provider manager
        services.TryAddSingleton<ITokenCountProvider, EstimatedUtf8TokenCountProvider>();
        services.TryAddSingleton<IAgentCapabilityStateStore, FileAgentCapabilityStateStore>();
        services.TryAddSingleton<IAgentCapabilityService, AgentCapabilityService>();
        services.TryAddSingleton<AIChatRuntimeContextAccessor>();
        services.TryAddSingleton<IAIChatRuntimeContextAccessor>(sp =>
            sp.GetRequiredService<AIChatRuntimeContextAccessor>());
        services.TryAddSingleton<AgentStreamingCoordinator>();
        services.TryAddSingleton<AgentResponseUpdateChannelContext>();
        services.AddSingleton<AIProviderRegistry>();
        services.AddSingleton<IAIProviderFactory>(sp => sp.GetRequiredService<AIProviderRegistry>());
        services.AddSingleton<IAIChatAgentFactory, AIChatAgentFactory>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAIChatAgentDecorator, ToolInvocationTrackingAgentDecorator>());

        // Register chat service
        services.AddSingleton<AIChatService>();
        services.AddHttpContextAccessor();
        services.TryAddSingleton<AIFileStore>();
        services.TryAddSingleton<IChatHistoryProvider, FileChatHistoryProvider>();
        services.TryAddScoped<IChatUserIdentityAccessor, HttpChatUserIdentityAccessor>();
        services.TryAddScoped<IChatHistoryPartitionResolver, ChatHistoryPartitionResolver>();
        services.TryAddSingleton<IChatDocumentExtractor, ChatDocumentExtractor>();
        services.TryAddSingleton<IChatAttachmentStore, FileChatAttachmentStore>();
        services.AddScoped<ChatAttachmentFacade>();
        services.AddScoped(sp => new ChatFacade(
            sp.GetRequiredService<AIChatService>(),
            sp.GetRequiredService<IAIProviderFactory>(),
            sp.GetRequiredService<IChatHistoryPartitionResolver>()));
        services.AddScoped(sp => new ChatHistoryFacade(
            sp.GetRequiredService<IChatHistoryProvider>(),
            sp.GetRequiredService<IChatHistoryPartitionResolver>(),
            sp.GetRequiredService<AIChatService>()));
        services.AddScoped<ProviderFacade>();
        services.AddScoped<AgentCapabilityFacade>();
    }
}

/// <summary>
/// Builder for AI module configuration.
/// </summary>
public static class ModuleAIRegistrationExtensions
{
    /// <summary>
    /// Replaces host-wide provider/model settings persistence. The singleton store must support revision-checked
    /// atomic writes; credentials reaching it have already been protected by the host's data-protection provider.
    /// Register the host data-protection key ring consistently across instances that share this store.
    /// </summary>
    /// <typeparam name="TStore">Thread-safe configuration storage implementation.</typeparam>
    /// <param name="module">The AI module registration to configure.</param>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleAI, ModuleAIOption> UseConfigurationStore<TStore>(
        this ModuleRegistration<ModuleAI, ModuleAIOption> module)
        where TStore : class, IAIConfigurationStore
    {
        module.ConfigureServices(context =>
        {
            context.Services.RemoveAll<IAIConfigurationStore>();
            context.Services.AddSingleton<IAIConfigurationStore, TStore>();
        });
        return module;
    }

    /// <summary>
    /// Replaces conversation attachment storage. The singleton store must isolate attachments by the supplied
    /// trusted partition and session, enforce upload limits, and publish complete data before returning a reference.
    /// Custom stores own the cleanup policy for abandoned uploads and must coordinate attachment retention
    /// and deletion with the configured chat history provider.
    /// </summary>
    /// <typeparam name="TStore">Thread-safe attachment storage implementation.</typeparam>
    /// <param name="module">The AI module registration to configure.</param>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleAI, ModuleAIOption> UseChatAttachmentStore<TStore>(
        this ModuleRegistration<ModuleAI, ModuleAIOption> module)
        where TStore : class, IChatAttachmentStore
    {
        module.ConfigureServices(context =>
        {
            context.Services.RemoveAll<IChatAttachmentStore>();
            context.Services.AddSingleton<IChatAttachmentStore, TStore>();
        });
        return module;
    }

    /// <summary>
    /// Replaces file-backed chat history and host identity partitioning with custom implementations.
    /// </summary>
    /// <typeparam name="TProvider">Scoped provider that owns durable snapshot storage.</typeparam>
    /// <typeparam name="TPartitionResolver">
    /// Scoped resolver that derives the current caller's isolated partition.
    /// </typeparam>
    /// <returns>The current module registration.</returns>
    /// <remarks>
    /// This method is optional. The default stores snapshots on disk inside the configured storage root.
    /// Custom server implementations must derive partitions from trusted user/workspace identity.
    /// The default history provider and attachment store share a file catalog, partition lock, and session
    /// directories to coordinate publication and deletion. A custom history provider requires a coordinated
    /// attachment store registered with <see cref="UseChatAttachmentStore{TStore}"/> because the default store
    /// checks the file catalog for active sessions. The replacement store must follow the history provider's
    /// archive and deletion lifecycle.
    /// </remarks>
    public static ModuleRegistration<ModuleAI, ModuleAIOption> UseChatHistoryProvider<TProvider, TPartitionResolver>(this ModuleRegistration<ModuleAI, ModuleAIOption> module)
        where TProvider : class, IChatHistoryProvider
        where TPartitionResolver : class, IChatHistoryPartitionResolver
    {
        module.ConfigureServices(context =>
        {
            context.Services.RemoveAll<IChatHistoryProvider>();
            context.Services.RemoveAll<IChatHistoryPartitionResolver>();
            context.Services.AddScoped<IChatHistoryProvider, TProvider>();
            context.Services.AddScoped<IChatHistoryPartitionResolver, TPartitionResolver>();
        });

        return module;
    }

    /// <summary>
    /// Registers code-defined defaults for an OpenAI-compatible provider, including Chat Completions or Responses.
    /// </summary>
    /// <param name="module">The AI module registration to configure.</param>
    /// <param name="configure">The configuration delegate.</param>
    /// <param name="providerId">Optional provider identifier. Defaults to provider type.</param>
    /// <returns>The current builder instance.</returns>
    /// <remarks>
    /// No network client is created during registration. Persisted host settings can override this definition;
    /// resetting an override restores these defaults. Use a stable provider identifier so saved conversations
    /// continue to resolve it. Running operations retain their captured provider while settings change.
    /// </remarks>
    public static ModuleRegistration<ModuleAI, ModuleAIOption> AddOpenAIProvider(this ModuleRegistration<ModuleAI, ModuleAIOption> module,
        Action<OpenAIProviderOptions> configure,
        string? providerId = null)
    {
        var options = new OpenAIProviderOptions { ApiKey = "", SupportedModels = [] };
        configure(options);
        options.ProviderId = providerId ?? options.ProviderId ?? nameof(EAIProviderType.OpenAI);

        module.ConfigureServices(context =>
        {
            context.Services.AddSingleton(new AIProviderDefinition(EAIProviderType.OpenAI, options));
        });

        return module;
    }

    /// <summary>
    /// Registers code-defined defaults for an Anthropic provider.
    /// </summary>
    /// <param name="module">The AI module registration to configure.</param>
    /// <param name="configure">The configuration delegate.</param>
    /// <param name="providerId">Optional provider identifier. Defaults to provider type.</param>
    /// <returns>The current builder instance.</returns>
    /// <remarks>
    /// No network client is created during registration. Persisted host settings can override this definition;
    /// resetting an override restores these defaults. Use a stable provider identifier so saved conversations
    /// continue to resolve it. Running operations retain their captured provider while settings change.
    /// </remarks>
    public static ModuleRegistration<ModuleAI, ModuleAIOption> AddAnthropicProvider(this ModuleRegistration<ModuleAI, ModuleAIOption> module,
        Action<AnthropicProviderOptions> configure,
        string? providerId = null)
    {
        var options = new AnthropicProviderOptions { ApiKey = "", SupportedModels = [] };
        configure(options);

        options.ProviderId = providerId ?? options.ProviderId ?? nameof(EAIProviderType.Anthropic);

        module.ConfigureServices(context =>
        {
            context.Services.AddSingleton(new AIProviderDefinition(EAIProviderType.Anthropic, options));
        });

        return module;
    }

    /// <summary>
    /// Add fake embedding provider.
    /// </summary>
    /// <param name="module">The AI module registration to configure.</param>
    /// <param name="configure">Options configure delegate.</param>
    /// <param name="providerId">Optional provider identifier. Defaults to provider type.</param>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleAI, ModuleAIOption> AddFakeProvider(this ModuleRegistration<ModuleAI, ModuleAIOption> module,
        Action<FakeProviderOptions> configure,
        string? providerId = null)
    {
        var options = new FakeProviderOptions { ApiKey = "fake", SupportedModels = [] };
        configure(options);
        options.ProviderId = providerId ?? options.ProviderId ?? nameof(EAIProviderType.Fake);

        module.ConfigureServices(context =>
        {
            context.Services.AddSingleton<IAIProvider>(serviceProvider =>
                new FakeProvider(options, serviceProvider.GetRequiredService<AIModelCatalog>()));
        });

        return module;
    }

    /// <summary>
    /// Adds an explicit model template referenced by code-defined providers' SupportedModels lists, including
    /// custom endpoints. Use provider-scoped Models definitions when the same identifier has different metadata
    /// across providers. Remote discovery does not apply unreferenced global templates to custom endpoints.
    /// </summary>
    /// <param name="module">The AI module registration to configure.</param>
    /// <param name="model">The model information to add.</param>
    /// <returns>The current builder instance.</returns>
    public static ModuleRegistration<ModuleAI, ModuleAIOption> AddModel(this ModuleRegistration<ModuleAI, ModuleAIOption> module, AIModelInfo model)
    {
        module.Configure(options => options.AddModel(model));
        return module;
    }

    /// <summary>
    /// Registers a host-owned singleton provider with its own implementation and configuration.
    /// </summary>
    /// <typeparam name="TProvider">Provider type</typeparam>
    /// <param name="module">The AI module registration to configure.</param>
    /// <param name="providerFactory">Provider factory method</param>
    /// <returns>The current builder instance.</returns>
    /// <remarks>
    /// Custom provider identifiers cannot be replaced by runtime provider settings. The host dependency-injection
    /// container owns disposal; consumers borrow the provider through <see cref="IAIProviderFactory"/> leases.
    /// </remarks>
    public static ModuleRegistration<ModuleAI, ModuleAIOption> AddProvider<TProvider>(this ModuleRegistration<ModuleAI, ModuleAIOption> module, Func<IServiceProvider, TProvider> providerFactory)
        where TProvider : class, IAIProvider
    {
        module.ConfigureServices(context =>
        {
            context.Services.AddSingleton<IAIProvider>(serviceProvider => providerFactory(serviceProvider));
        });

        return module;
    }

}

/// <summary>
/// Options for the AI module.
/// </summary>
public class ModuleAIOption : ModuleOptions<ModuleAI>
{
    /// <summary>
    /// Root directory for host settings, capability enablement, conversation snapshots, and attachments. Defaults to
    /// <c>monica_data/ai</c> relative to the process working directory. Configure an absolute writable
    /// path when the application runs from different working directories or uses a dedicated data volume.
    /// </summary>
    public string StorageRootPath { get; set; } = "monica_data/ai";

    /// <summary>
    /// Stable host workspace identifier used with authenticated user identity for conversation isolation.
    /// Defaults to <c>default</c>. Anonymous standalone hosts share this workspace. Configure separate
    /// identifiers for independently isolated workspaces that use the same storage provider.
    /// </summary>
    public string WorkspaceId { get; set; } = "default";

    /// <summary>
    /// Behavior when registered provider or model configuration fails validation at startup. Defaults to
    /// <see cref="AIConfigurationValidationMode.Disable"/>, which keeps the host running with the affected
    /// provider disabled and its validation reasons exposed in the provider management UI and diagnostics.
    /// Choose <see cref="AIConfigurationValidationMode.Throw"/> to fail startup instead. Runtime settings
    /// edits always validate strictly regardless of this mode.
    /// </summary>
    public AIConfigurationValidationMode ConfigurationValidationMode { get; set; } = AIConfigurationValidationMode.Disable;

    /// <summary>Maximum unencoded size of one chat attachment. Defaults to 20 MiB.</summary>
    public long MaxChatAttachmentBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>
    /// Maximum extracted document length, in UTF-16 characters. Defaults to 200,000; documents exceeding
    /// the limit are rejected rather than truncated. Increase only when the configured model can accept the text.
    /// </summary>
    public int MaxExtractedDocumentCharacters { get; set; } = 200_000;

    internal List<AIModelInfo> ModelRegistrations { get; } = [];

    internal static IReadOnlyList<AIModelInfo> GetReservedModels()
    {
        return [..OpenAIReservedModels.Models, ..AnthropicReservedModels.Models];
    }

    /// <summary>
    /// Adds an explicit model template for code-defined providers that reference its name in SupportedModels.
    /// This metadata applies to custom endpoints too; prefer provider-scoped Models for differing provider values.
    /// </summary>
    public void AddModel(AIModelInfo model)
    {
        ModelRegistrations.Add(model);
    }

    /// <summary>
    /// Default system prompt.
    /// </summary>
    public string? DefaultSystemPrompt { get; set; }

}
