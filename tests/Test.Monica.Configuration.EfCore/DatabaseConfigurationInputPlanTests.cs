using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Configuration.Models;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Annotations;
using Monica.Configuration.Bootstrap;
using Monica.Core.Modularity.Extensions;
using Monica.DependencyInjection.Abstractions;
using Monica.DependencyInjection.Services;
using Monica.Modules;
using Monica.Repository.Entity.Abstractions;
using Xunit;

namespace Test.Monica.Configuration.EfCore;

public sealed partial class DatabaseConfigurationStorePublishTests
{
    [Fact]
    public async Task InputPlan_WhenStartupOptionsAreRead_ShouldKeepManagedStoreUnwrittenUntilRuntimeActivation()
    {
        var databasePath = await CreateMigratedDatabaseAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["StartupInputPlan:Enabled"] = "true";
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseDbConfigurationStore(options =>
                ConfigurationStoreTestContextFactory.ConfigureOptions(
                    options,
                    $"Data Source={databasePath}")));
        builder.Configuration["DeploymentInputPlan:Endpoint"] = "https://initial.test";
        DeploymentInputPlanOptions? startup = null;

        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options
                .ExcludeDefault()
                .Add(typeof(StartupInputPlanOptions).Assembly));
            startup = monica.GetStartupOptions<DeploymentInputPlanOptions>();
            monica.AddConfiguration(inputPlan);
        });
        builder.Services.AddScoped<ICachedServiceProvider, CachedServiceProvider>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IAuditPropertySetter, NoOpAuditPropertySetter>());
        using var runtimeHost = builder.Build();
        builder.Configuration["DeploymentInputPlan:Endpoint"] = "https://candidate.test";
        startup!.Endpoint.Should().Be("https://initial.test");
        runtimeHost.Services.GetRequiredService<IOptions<DeploymentInputPlanOptions>>().Value.Should().BeSameAs(startup);
        var runtimeStore = runtimeHost.Services.GetRequiredService<IConfigurationEffectiveValueStore>();

        runtimeStore.Descriptor.StoreKey.Should().Be("db:default");
        runtimeHost.Services.GetRequiredService<IConfigurationEffectiveValueReader>()
            .Should().BeSameAs(runtimeStore);
        var documentBeforeActivation = await runtimeStore.GetAsync(
            "test.configuration.startup-input-plan",
            TestContext.Current.CancellationToken);
        documentBeforeActivation.Should().BeNull();

        await runtimeHost.StartAsync(TestContext.Current.CancellationToken);

        var runtimeDocument = await runtimeStore.GetAsync(
            "test.configuration.startup-input-plan",
            TestContext.Current.CancellationToken);
        runtimeDocument.Should().NotBeNull();
        var runtimeJson = JsonNode.Parse(runtimeDocument!.Json)!.AsObject();
        runtimeJson["Enabled"]!.GetValue<bool>().Should().BeTrue();
        (await runtimeStore.GetAsync("test.configuration.deployment-input-plan",
            TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Configuration("StartupInputPlan", DefinitionKey = "test.configuration.startup-input-plan")]
    private sealed class StartupInputPlanOptions
    {
        public bool Enabled { get; set; }
    }

    [Configuration("DeploymentInputPlan", DefinitionKey = "test.configuration.deployment-input-plan",
        Kind = ConfigurationKind.Startup)]
    private sealed class DeploymentInputPlanOptions
    {
        public string Endpoint { get; set; } = "https://default.test";
    }
}
