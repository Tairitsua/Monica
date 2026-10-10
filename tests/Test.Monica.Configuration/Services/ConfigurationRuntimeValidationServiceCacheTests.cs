using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Metrics;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class ConfigurationRuntimeValidationServiceCacheTests
{
    [Fact]
    public async Task Reports_ShouldBeCachedUntilTheProviderCommitsAnotherProjection()
    {
        var definition = TestConfigurationFactory.Definition();
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(definition);
        var document = new ConfigurationEffectiveValueDocument
        {
            DefinitionKey = definition.DefinitionKey,
            Json = """{"WorkerId":1}""",
            Version = 1,
            SchemaVersion = definition.SchemaVersion,
            LastModifiedTime = DateTimeOffset.UtcNow
        };
        var failReload = false;
        var store = Substitute.For<IConfigurationEffectiveValueStore>();
        store.Descriptor.Returns(new ConfigurationStoreDescriptor
        {
            StoreKey = "test:runtime-validation",
            DisplayName = "Runtime validation test store",
            Kind = ConfigurationStoreKind.File,
            SupportsEffectiveValues = true
        });
        store.EnsureCreatedAsync(
                Arg.Any<IReadOnlyList<ConfigurationEffectiveValueSeed>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => failReload
                ? Task.FromException<IReadOnlyList<ConfigurationEffectiveValueDocument>>(
                    new InvalidOperationException("Reload failed."))
                : Task.FromResult<IReadOnlyList<ConfigurationEffectiveValueDocument>>([document]));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Test:App:WorkerId"] = "1"
            })
            .Build();
        var runtimeContext = new ConfigurationRuntimeContext();
        runtimeContext.Capture(configuration);
        var accessor = new MonicaConfigurationProviderAccessor();
        var provider = CreateProvider(accessor, registry, runtimeContext, store);
        using var validationService = new ConfigurationRuntimeValidationService(
            registry,
            ConfigurationValidationTestServices.CreateCoordinator(),
            Substitute.For<IConfigurationSourceInspector>(),
            accessor,
            runtimeContext);

        var initialFullReport = validationService.GetReport();
        var initialDefinitionReport = validationService.GetReport(definition.DefinitionKey);

        validationService.GetReport().Should().BeSameAs(initialFullReport);
        validationService.GetReport(definition.DefinitionKey).Should().BeSameAs(initialDefinitionReport);

        await provider.ReloadAsync(CancellationToken.None);

        provider.SuccessfulProjectionRevision.Should().Be(1);
        var reloadedFullReport = validationService.GetReport();
        var reloadedDefinitionReport = validationService.GetReport(definition.DefinitionKey);
        reloadedFullReport.Should().NotBeSameAs(initialFullReport);
        reloadedDefinitionReport.Should().NotBeSameAs(initialDefinitionReport);
        validationService.GetReport().Should().BeSameAs(reloadedFullReport);
        validationService.GetReport(definition.DefinitionKey).Should().BeSameAs(reloadedDefinitionReport);

        configuration.Reload();

        var runtimeReloadedReport = validationService.GetReport();
        var runtimeReloadedDefinitionReport = validationService.GetReport(definition.DefinitionKey);
        runtimeReloadedReport.Should().NotBeSameAs(reloadedFullReport);
        runtimeReloadedDefinitionReport.Should().NotBeSameAs(reloadedDefinitionReport);
        validationService.GetReport().Should().BeSameAs(runtimeReloadedReport);

        failReload = true;
        var act = () => provider.ReloadAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        provider.SuccessfulProjectionRevision.Should().Be(1);
        validationService.GetReport().Should().BeSameAs(runtimeReloadedReport);
        validationService.GetReport(definition.DefinitionKey).Should().BeSameAs(runtimeReloadedDefinitionReport);
    }

    private static MonicaConfigurationProvider CreateProvider(
        MonicaConfigurationProviderAccessor accessor,
        IConfigurationDefinitionRegistry registry,
        ConfigurationRuntimeContext runtimeContext,
        IConfigurationEffectiveValueStore store)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton(runtimeContext);
        services.AddSingleton(registry);
        services.AddSingleton(store);
        ConfigurationValidationTestServices.AddCoreServices(services);
        services.AddSingleton<ConfigurationStoredValueCodec>();
        services.AddSingleton<ConfigurationEffectiveValuePatchEngine>();
        services.AddSingleton<ConfigurationEffectiveValueDocumentEditor>();
        services.AddSingleton<IConfigurationStoreStateTracker, ConfigurationStoreStateTracker>();
        services.AddSingleton<ConfigurationMetricsRecorder>();

        var provider = new MonicaConfigurationProvider(accessor);
        accessor.Provider = provider;
        accessor.ServiceProvider = services.BuildServiceProvider();
        return provider;
    }
}
