using AwesomeAssertions;
using Monica.AI.Configuration;
using Monica.AI.Configuration.Models;
using Monica.AI.Configuration.Services;
using Monica.AI.Models;
using Monica.AI.Providers;
using Monica.AI.Providers.OpenAI;
using Monica.AI.Services;
using Monica.Modules;
using Test.Monica.AI.Support;

namespace Test.Monica.AI.Configuration;

public sealed class AIConfigurationStartupValidationTests
{
    [Fact]
    public void LoadDefaults_WhenModelCapacitiesAreInvalid_ShouldDisableProviderInsteadOfThrowing()
    {
        using var workspace = new ConfigurationTestWorkspace();

        var service = workspace.CreateService(InvalidCapacityProvider());

        var provider = service.Resolve().Providers.Should().ContainSingle().Which;
        provider.Configuration.Enabled.Should().BeFalse();
        provider.ValidationErrors.Should().ContainSingle()
            .Which.Should().Contain("DeepSeek-V4-Flash").And.Contain("positive capacities");
        service.GetSnapshot().Providers.Single().ValidationErrors.Should().HaveCount(1);
    }

    [Fact]
    public void LoadDefaults_WhenModeIsThrow_ShouldFailConstruction()
    {
        using var workspace = new ConfigurationTestWorkspace();

        var act = () => workspace.CreateService(new ModuleAIOption { ConfigurationValidationMode = AIConfigurationValidationMode.Throw },
            InvalidCapacityProvider());

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("DeepSeek-V4-Flash");
    }

    [Fact]
    public void LoadDefaults_WhenSeveralModelsAreInvalid_ShouldReportEveryFinding()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var definition = new AIProviderDefinition(EAIProviderType.OpenAI, new OpenAIProviderOptions
        {
            ProviderId = "deepseek",
            ApiKey = "key",
            Models =
            [
                new LLMModelInfo { ModelName = "first", ContextWindow = 256000, MaxOutputTokens = 256000 },
                new LLMModelInfo { ModelName = "second", ContextWindow = 256000, MaxOutputTokens = 256000 }
            ]
        });

        var service = workspace.CreateService(definition);

        var provider = service.Resolve().Providers.Should().ContainSingle().Which;
        provider.ValidationErrors.Should().HaveCount(2)
            .And.Contain(findings => findings.Contains("first")).And.Contain(findings => findings.Contains("second"));
    }

    [Fact]
    public async Task LoadSettings_WhenPersistedOverrideIsInvalid_ShouldDisableOverrideInsteadOfThrowing()
    {
        using var workspace = new ConfigurationTestWorkspace();
        // Interactive edits reject invalid settings; a stale or externally written document must not stop the host.
        await workspace.Store.WriteAsync(new AIConfigurationDocument
        {
            Providers = [new AIPersistedProvider
            {
                Configuration = ValidOverride() with
                {
                    Models = [new AIModelConfiguration { ModelName = "broken", ContextWindow = 100, MaxOutputTokens = 100 }]
                }
            }]
        }, 0, TestContext.Current.CancellationToken);

        var restarted = workspace.CreateService();
        var degraded = restarted.GetSnapshot().Providers.Single();
        degraded.Configuration.Enabled.Should().BeFalse();
        degraded.ValidationErrors.Should().ContainSingle().Which.Should().Contain("broken");
        restarted.Resolve().Providers.Single().Configuration.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task LoadSettings_WhenValidOverrideReplacesInvalidDefault_ShouldClearValidationErrors()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var healthy = workspace.CreateService(InvalidCapacityProvider());
        await healthy.UpsertAsync(ValidOverride(), "key", false, 0, TestContext.Current.CancellationToken);

        var restarted = workspace.CreateService(InvalidCapacityProvider());
        var provider = restarted.Resolve().Providers.Should().ContainSingle().Which;
        provider.Configuration.Enabled.Should().BeTrue("a valid persisted override replaces the invalid code default");
        provider.ValidationErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_WhenModeDisablesAtStartup_ShouldStillRejectInvalidRuntimeEdits()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var service = workspace.CreateService();

        var act = () => service.UpsertAsync(ValidOverride() with
        {
            Models = [new AIModelConfiguration { ModelName = "broken", ContextWindow = 100, MaxOutputTokens = 100 }]
        }, "key", false, 0, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("broken");
    }

    [Fact]
    public void Registry_WhenStartupValidationDisabledAProvider_ShouldExposeConfigurationErrors()
    {
        using var workspace = new ConfigurationTestWorkspace();
        var service = workspace.CreateService(InvalidCapacityProvider());
        using var registry = new AIProviderRegistry([], service, workspace.Catalog);

        var info = registry.GetAllProviderInfos().Should().ContainSingle().Which;
        info.IsValid.Should().BeFalse();
        info.Status.Should().Be(AIProviderStatus.ConfigurationError);
        info.ConfigurationErrors.Should().Contain(error => error.Contains("DeepSeek-V4-Flash"));
    }

    private static AIProviderDefinition InvalidCapacityProvider() => new(
        EAIProviderType.OpenAI, new OpenAIProviderOptions
        {
            ProviderId = "deepseek",
            ApiKey = "key",
            Models = [new LLMModelInfo { ModelName = "DeepSeek-V4-Flash", ContextWindow = 256000, MaxOutputTokens = 256000 }]
        });

    private static AIProviderConfiguration ValidOverride() => new()
    {
        ProviderId = "deepseek", ProviderType = EAIProviderType.OpenAI, BaseUrl = "https://example.test/v1",
        Models = [new AIModelConfiguration { ModelName = "custom-model" }]
    };
}
