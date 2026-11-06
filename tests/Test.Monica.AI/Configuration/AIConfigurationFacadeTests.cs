using System.Text.Json;
using AwesomeAssertions;
using Monica.AI.Abstractions;
using Monica.AI.Configuration.Facades;
using Monica.AI.Configuration.Models;
using Monica.AI.Configuration.Services;
using Monica.AI.Providers;
using Monica.AI.Services;
using Monica.AI.Models;
using NSubstitute;
using Monica.Core.Results;
using Test.Monica.AI.Support;

namespace Test.Monica.AI.Configuration;

public sealed class AIConfigurationFacadeTests
{
    [Fact]
    public async Task UpsertProviderAsync_WhenKeyIsSaved_ShouldProtectDiskAndRestoreAfterRestart()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var configuration = workspace.CreateService();
        using var registry = new AIProviderRegistry([], configuration, workspace.Catalog);
        var facade = new AIConfigurationFacade(configuration, registry, workspace.Catalog);

        var result = await facade.UpsertProviderAsync(Provider("one") with { OpenAIProtocolProfile = OpenAIProtocolProfile.DeepSeek },
            "test-super-secret", false, 0, TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Revision.Should().Be(1);
        result.Data.Providers.Should().ContainSingle().Which.HasApiKey.Should().BeTrue();
        JsonSerializer.Serialize(result.Data).Should().NotContain("test-super-secret").And.NotContain("ProtectedApiKey");
        File.ReadAllText(workspace.Files.GetPath("configuration/providers.json")).Should().NotContain("test-super-secret");

        var restarted = workspace.CreateService();
        restarted.Resolve().Providers.Should().ContainSingle().Which.ApiKey.Should().Be("test-super-secret");
        restarted.GetSnapshot().Providers.Single().Configuration.Models.Single().ModelName.Should().Be("custom-model");
        restarted.GetSnapshot().Providers.Single().Configuration.OpenAIProtocolProfile.Should().Be(OpenAIProtocolProfile.DeepSeek);
    }

    [Fact]
    public async Task UpsertProviderAsync_WhenConcurrentRevisionIsStale_ShouldRejectAndPreserveWinner()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var first = workspace.CreateService();
        var second = workspace.CreateService();
        using var registry = new AIProviderRegistry([], second, workspace.Catalog);
        var facade = new AIConfigurationFacade(second, registry, workspace.Catalog);
        await first.UpsertAsync(Provider("winner"), "key", false, 0, TestContext.Current.CancellationToken);

        var result = await facade.UpsertProviderAsync(Provider("stale"), "key", false, 0, TestContext.Current.CancellationToken);

        result.Status.Should().NotBe(ResultStatus.Ok);
        result.Message.Should().Contain("Reload settings");
        result.Data.Should().BeNull();
        workspace.Store.Read().Providers.Single().Configuration.ProviderId.Should().Be("winner");
        second.GetSnapshot().Revision.Should().Be(1);
    }

    [Fact]
    public async Task ResetProviderAsync_WhenCodeDefaultIsOverridden_ShouldRestoreConfigurationAndCodeKey()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var definition = new AIProviderDefinition(EAIProviderType.OpenAI, new OpenAIProviderOptions
        {
            ProviderId = "code", ApiKey = "code-key", SupportedModels = ["gpt-4o"], SystemPrompt = "code prompt"
        });
        var configuration = workspace.CreateService(definition);
        using var registry = new AIProviderRegistry([], configuration, workspace.Catalog);
        var facade = new AIConfigurationFacade(configuration, registry, workspace.Catalog);
        await facade.UpsertProviderAsync(Provider("code") with { Enabled = false }, "override-key", false, 0, TestContext.Current.CancellationToken);

        var result = await facade.ResetProviderAsync("code", 1, TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        var provider = result.Data!.Providers.Single();
        provider.IsCodeDefined.Should().BeTrue();
        provider.HasOverride.Should().BeFalse();
        provider.Configuration.Enabled.Should().BeTrue();
        provider.Configuration.SystemPrompt.Should().Be("code prompt");
        configuration.Resolve().Providers.Single().ApiKey.Should().Be("code-key");
    }

    [Fact]
    public async Task UpsertProviderAsync_WhenKeyIsExplicitlyCleared_ShouldNotFallBackToCodeKey()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var configuration = workspace.CreateService(new AIProviderDefinition(EAIProviderType.OpenAI, new OpenAIProviderOptions
        {
            ProviderId = "code", ApiKey = "code-key", SupportedModels = ["gpt-4o"]
        }));
        var result = await configuration.UpsertAsync(Provider("code"), null, true, 0, TestContext.Current.CancellationToken);

        result.Providers.Single().HasApiKey.Should().BeFalse();
        configuration.Resolve().Providers.Single().ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task UpsertProviderAsync_WhenSameModelIdHasDifferentProviders_ShouldKeepMetadataIsolated()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var configuration = workspace.CreateService();
        await configuration.UpsertAsync(Provider("small") with
        {
            Models = [new AIModelConfiguration { ModelName = "same-model", ContextWindow = 32000, SupportsImage = false }]
        }, "key", false, 0, TestContext.Current.CancellationToken);
        await configuration.UpsertAsync(Provider("large") with
        {
            Models = [new AIModelConfiguration { ModelName = "same-model", ContextWindow = 128000, SupportsImage = true }]
        }, "key", false, 1, TestContext.Current.CancellationToken);
        using var registry = new AIProviderRegistry([], configuration, workspace.Catalog);

        var small = registry.GetProviderInfo("small")!.SupportedModels!.OfType<LLMModelInfo>().Single();
        var large = registry.GetProviderInfo("large")!.SupportedModels!.OfType<LLMModelInfo>().Single();

        small.ContextWindow.Should().Be(32000);
        small.SupportsImage.Should().BeFalse();
        large.ContextWindow.Should().Be(128000);
        large.SupportsImage.Should().BeTrue();
        large.SupportsTools.Should().BeNull();
    }

    [Fact]
    public async Task UpsertProviderAsync_WhenNewDefaultIsSelected_ShouldAtomicallyClearInheritedDefault()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var configuration = workspace.CreateService(new AIProviderDefinition(EAIProviderType.OpenAI, new OpenAIProviderOptions
        {
            ProviderId = "code", ApiKey = "code-key", SupportedModels = ["gpt-4o"], IsDefault = true
        }));

        var result = await configuration.UpsertAsync(Provider("selected") with { IsDefault = true }, "key", false, 0, TestContext.Current.CancellationToken);

        result.Providers.Count(static provider => provider.Configuration.IsDefault).Should().Be(1);
        result.Providers.Single(static provider => provider.Configuration.ProviderId == "selected").Configuration.IsDefault.Should().BeTrue();
        configuration.Resolve().Providers.Single(static provider => provider.Configuration.ProviderId == "code").ApiKey.Should().Be("code-key");
    }

    [Fact]
    public async Task UpsertProviderAsync_WhenEndpointContainsCredential_ShouldRejectWithoutPersisting()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var configuration = workspace.CreateService();
        using var registry = new AIProviderRegistry([], configuration, workspace.Catalog);
        var facade = new AIConfigurationFacade(configuration, registry, workspace.Catalog);

        var result = await facade.UpsertProviderAsync(Provider("unsafe") with
        {
            BaseUrl = "https://user:embedded-secret@example.test/v1"
        }, "key", false, 0, TestContext.Current.CancellationToken);

        result.Status.Should().NotBe(ResultStatus.Ok);
        result.Message.Should().NotContain("embedded-secret");
        workspace.Store.Read().Revision.Should().Be(0);
    }

    internal static AIProviderConfiguration Provider(string id) => new()
    {
        ProviderId = id, ProviderType = EAIProviderType.OpenAI, BaseUrl = "https://example.test/v1",
        Models = [new AIModelConfiguration { ModelName = "custom-model" }]
    };

    [Fact]
    public async Task DiscoverModelsAsync_WhenEndpointProvidesEvidence_ShouldFillUnknownsAndRespectOverrides()
    {
        using var workspace = new ConfigurationTestWorkspace();
        workspace.Catalog.AddModel(new LLMModelInfo { ModelName = "gpt-4o", ContextWindow = 65536, SupportsImage = true });
        var configuration = workspace.CreateService();
        await configuration.UpsertAsync(Provider("provider") with
        {
            Models = [new AIModelConfiguration { ModelName = "custom-model", SupportsImage = false, ContextWindow = 64000 }]
        }, "key", false, 0, TestContext.Current.CancellationToken);
        var remote = Substitute.For<IAIProvider>();
        remote.FetchRemoteModelsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AIRemoteModelInfo>>([
            new AIRemoteModelInfo
            {
                ModelId = "custom-model",
                Configuration = new AIModelConfiguration
                {
                    ModelName = "custom-model", SupportsImage = true, SupportsDocuments = true,
                    ContextWindow = 200000, MaxOutputTokens = 32000
                }
            },
            new AIRemoteModelInfo { ModelId = "gpt-4o" }
        ]));
        var lease = Substitute.For<IAIProviderLease>();
        lease.Provider.Returns(remote);
        var providers = Substitute.For<IAIProviderFactory>();
        providers.AcquireProvider("provider").Returns(lease);
        var facade = new AIConfigurationFacade(configuration, providers, workspace.Catalog);

        var result = await facade.DiscoverModelsAsync("provider", TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        var configured = result.Data!.Single(static model => model.ModelName == "custom-model");
        configured.SupportsImage.Should().BeFalse();
        configured.SupportsDocuments.Should().BeTrue();
        configured.ContextWindow.Should().Be(64000);
        configured.MaxOutputTokens.Should().Be(32000);
        result.Data!.Single(static model => model.ModelName == "gpt-4o").SupportsImage.Should().BeNull();
        result.Data!.Single(static model => model.ModelName == "gpt-4o").ContextWindow.Should().BeNull();
        workspace.Store.Read().Revision.Should().Be(1);
        lease.Received(1).Dispose();
    }

    [Fact]
    public async Task UpsertProviderAsync_WhenOpenAIFixedBudgetIsConfigured_ShouldRejectUnsupportedMapping()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var configuration = workspace.CreateService();
        using var registry = new AIProviderRegistry([], configuration, workspace.Catalog);
        var facade = new AIConfigurationFacade(configuration, registry, workspace.Catalog);
        var provider = Provider("provider") with
        {
            Models = [new AIModelConfiguration
            {
                ModelName = "custom-model",
                ReasoningLevels = [new AIReasoningLevel { Id = "high", BudgetTokens = 8192 }]
            }]
        };

        var result = await facade.UpsertProviderAsync(provider, "key", false, 0, TestContext.Current.CancellationToken);

        result.Status.Should().NotBe(ResultStatus.Ok);
        result.Message.Should().Contain("no standard thinking-token budget");
        result.Data.Should().BeNull();
        workspace.Store.Read().Revision.Should().Be(0);
    }
}
