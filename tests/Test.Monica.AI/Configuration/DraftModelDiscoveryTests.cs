using AwesomeAssertions;
using Monica.AI.Configuration.Facades;
using Monica.AI.Configuration.Models;
using Monica.AI.Configuration.Services;
using Monica.AI.Providers;
using Monica.AI.Services;
using Monica.Core.Results;
using Test.Monica.AI.Support;

namespace Test.Monica.AI.Configuration;

public sealed class DraftModelDiscoveryTests
{
    [Fact]
    public async Task UnsavedDiscovery_ShouldUseDraftConnectionAndLeaveStoreAndRegistryUntouched()
    {
        await using var endpoint = new LoopbackModelEndpoint((_, _) => Task.FromResult((200, MODELS)));
        using var workspace = new ConfigurationTestWorkspace();
        var settings = workspace.CreateService();
        using var registry = new AIProviderRegistry([], settings, workspace.Catalog);
        var facade = new AIConfigurationFacade(settings, registry, workspace.Catalog);

        var result = await facade.DiscoverDraftModelsAsync(new AIProviderConfiguration { ProviderId = "", BaseUrl = endpoint.BaseUrl },
            "draft-key", ct: TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.Ok);
        (await endpoint.Credential.Task).Should().Be("Bearer draft-key");
        result.Data.Should().ContainSingle();
        result.Data![0].ContextWindow.Should().Be(131072);
        result.Data[0].SupportsImage.Should().BeTrue();
        result.Data[0].SupportsTools.Should().BeNull();
        settings.CurrentRevision.Should().Be(0);
        workspace.Store.Read().Providers.Should().BeEmpty();
        registry.GetAllProviderInfos().Should().BeEmpty();
    }

    [Fact]
    public async Task EditingDiscovery_ShouldUseSavedKeyOnlyWhenExplicitAndPreserveMetadataOverrides()
    {
        await using var endpoint = new LoopbackModelEndpoint((_, _) => Task.FromResult((200, MODELS)));
        using var workspace = new ConfigurationTestWorkspace();
        var settings = workspace.CreateService(new AIProviderDefinition(EAIProviderType.OpenAI,
            new OpenAIProviderOptions { ProviderId = "saved", ApiKey = "code-key", BaseUrl = "https://old.invalid" }));
        using var registry = new AIProviderRegistry([], settings, workspace.Catalog);
        var facade = new AIConfigurationFacade(settings, registry, workspace.Catalog);
        var draft = new AIProviderConfiguration
        {
            ProviderId = "saved", BaseUrl = endpoint.BaseUrl,
            Models = [new AIModelConfiguration { ModelName = "custom-alias", ContextWindow = 65536, SupportsImage = false }]
        };
        var missing = await facade.DiscoverDraftModelsAsync(draft, ct: TestContext.Current.CancellationToken);
        missing.Status.Should().NotBe(ResultStatus.Ok);
        endpoint.Credential.Task.IsCompleted.Should().BeFalse();

        var result = await facade.DiscoverDraftModelsAsync(draft, useSavedApiKey: true, ct: TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.Ok);
        (await endpoint.Credential.Task).Should().Be("Bearer code-key");
        result.Data!.Single().ContextWindow.Should().Be(65536);
        result.Data!.Single().SupportsImage.Should().BeFalse();
        result.Data!.Single().MaxOutputTokens.Should().Be(8192);
        settings.GetSnapshot().Providers.Single().Configuration.BaseUrl.Should().Be("https://old.invalid");
        workspace.Store.Read().Providers.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedDiscovery_ShouldRedactTransientCredentialAndKeepDraftUnpersisted()
    {
        await using var endpoint = new LoopbackModelEndpoint((_, _) => Task.FromResult((401,
            """{"error":{"message":"Authentication rejected draft-secret","type":"authentication_error"}}""")));
        using var workspace = new ConfigurationTestWorkspace();
        var settings = workspace.CreateService();
        using var registry = new AIProviderRegistry([], settings, workspace.Catalog);
        var facade = new AIConfigurationFacade(settings, registry, workspace.Catalog);

        var result = await facade.DiscoverDraftModelsAsync(new AIProviderConfiguration { ProviderId = "draft", BaseUrl = endpoint.BaseUrl },
            "draft-secret", ct: TestContext.Current.CancellationToken);

        result.Status.Should().NotBe(ResultStatus.Ok);
        result.Message.Should().NotContain("draft-secret").And.Contain("[redacted]");
        workspace.Store.Read().Revision.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverDraftModelsAsync_WhenReplacementKeyIsSupplied_ShouldPreferItWithoutChangingSavedKey()
    {
        await using var endpoint = new LoopbackModelEndpoint((_, _) => Task.FromResult((200, MODELS)));
        using var workspace = new ConfigurationTestWorkspace();
        var settings = workspace.CreateService(new AIProviderDefinition(EAIProviderType.OpenAI,
            new OpenAIProviderOptions { ProviderId = "saved", ApiKey = "saved-key" }));
        using var registry = new AIProviderRegistry([], settings, workspace.Catalog);
        var facade = new AIConfigurationFacade(settings, registry, workspace.Catalog);

        var result = await facade.DiscoverDraftModelsAsync(
            new AIProviderConfiguration { ProviderId = "saved", BaseUrl = endpoint.BaseUrl },
            "replacement-key", useSavedApiKey: true, ct: TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().ContainSingle();
        (await endpoint.Credential.Task).Should().Be("Bearer replacement-key");
        settings.Resolve().Providers.Single().ApiKey.Should().Be("saved-key");
        workspace.Store.Read().Revision.Should().Be(0);
    }

    [Fact]
    public async Task CancelledDiscovery_ShouldPropagateCancellationWithoutSaving()
    {
        await using var endpoint = new LoopbackModelEndpoint(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return (200, MODELS);
        });
        using var workspace = new ConfigurationTestWorkspace();
        var settings = workspace.CreateService();
        using var registry = new AIProviderRegistry([], settings, workspace.Catalog);
        var facade = new AIConfigurationFacade(settings, registry, workspace.Catalog);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var fetching = facade.DiscoverDraftModelsAsync(new AIProviderConfiguration { ProviderId = "draft", BaseUrl = endpoint.BaseUrl }, "key", ct: cancellation.Token);
        await endpoint.Credential.Task.WaitAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        var observe = () => fetching;
        await observe.Should().ThrowAsync<OperationCanceledException>();
        workspace.Store.Read().Revision.Should().Be(0);
    }

    private const string MODELS = """
        {"object":"list","data":[{"id":"custom-alias","object":"model","created":0,"owned_by":"test","context_length":131072,"max_output_tokens":8192,"capabilities":{"vision":true}}]}
        """;
}
