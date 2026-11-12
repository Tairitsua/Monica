using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Configuration.Annotations;
using Monica.Configuration.Binding;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.Models;
using Monica.Core.Modularity.Extensions;
using Xunit;

namespace Test.Monica.Configuration.Binding;

public class MonicaConfigurationCollectionBindingTests
{
    [Fact]
    public void StartupBinding_WhenConfiguredListHasDefaults_ShouldReplaceDefaultItems()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Bootstrap:Items:0"] = "configured"
        });

        var options = ReadStartupOptions<BootstrapListOptions>(configuration);

        options.Items.Should().Equal("configured");
    }

    [Fact]
    public void StartupBinding_WhenListSectionIsMissing_ShouldPreserveDefaultItems()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["MissingList:Name"] = "configured"
        });

        var options = ReadStartupOptions<MissingListOptions>(configuration);

        options.Items.Should().Equal("default");
    }

    [Fact]
    public void StartupBinding_WhenNestedListHasDefaults_ShouldReplaceNestedDefaultItems()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Nested:Nested:Items:0"] = "configured"
        });

        var options = ReadStartupOptions<NestedCollectionOptions>(configuration);

        options.Nested.Items.Should().Equal("configured");
    }

    [Fact]
    public void StartupBinding_WhenConfiguredDictionaryHasDefaults_ShouldReplaceDefaultEntries()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Dictionary:Map:configured"] = "value"
        });

        var options = ReadStartupOptions<DictionaryOptions>(configuration);

        options.Map.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("configured", "value"));
    }

    [Fact]
    public void StartupBinding_WhenCollectionUsesConfigurationKeyName_ShouldReplaceDefaultItems()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Alias:ConfiguredItems:0"] = "configured"
        });

        var options = ReadStartupOptions<AliasOptions>(configuration);

        options.Items.Should().Equal("configured");
    }

    [Fact]
    public void RuntimeOptionsBinding_WhenConfiguredListHasDefaults_ShouldReplaceDefaultItems()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Runtime:Items:0"] = "configured"
        });
        MonicaConfigurationBinder.BindOptions(
            builder.Services.AddOptions<RuntimeOptions>(),
            builder.Configuration.GetSection("Runtime"));

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<RuntimeOptions>>().Value;

        options.Items.Should().Equal("configured");
    }

    [Fact]
    public void RuntimeOptionsBinding_WhenConfigurationReloads_ShouldRefreshOptionsMonitor()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Runtime:Items:0"] = "initial"
        });
        MonicaConfigurationBinder.BindOptions(
            builder.Services.AddOptions<RuntimeOptions>(),
            builder.Configuration.GetSection("Runtime"));
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<RuntimeOptions>>();

        monitor.CurrentValue.Items.Should().Equal("initial");
        builder.Configuration["Runtime:Items:0"] = "updated";
        ((IConfigurationRoot)builder.Configuration).Reload();

        monitor.CurrentValue.Items.Should().Equal("updated");
    }

    private static TOptions ReadStartupOptions<TOptions>(IConfiguration configuration) where TOptions : class, new()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        TOptions? actual = null;
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            actual = monica.GetStartupOptions<TOptions>();
        });
        using var host = builder.Build();
        host.Services.GetRequiredService<IOptions<TOptions>>().Value.Should().BeSameAs(actual);
        return actual!;
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    [Configuration("Bootstrap", Kind = ConfigurationKind.Startup)]
    private sealed class BootstrapListOptions
    {
        public List<string> Items { get; set; } = ["default"];
    }

    [Configuration("MissingList", Kind = ConfigurationKind.Startup)]
    private sealed class MissingListOptions
    {
        public string? Name { get; set; }

        public List<string> Items { get; set; } = ["default"];
    }

    [Configuration("Nested", Kind = ConfigurationKind.Startup)]
    private sealed class NestedCollectionOptions
    {
        public NestedOptions Nested { get; set; } = new();
    }

    private sealed class NestedOptions
    {
        public List<string> Items { get; set; } = ["default"];
    }

    [Configuration("Dictionary", Kind = ConfigurationKind.Startup)]
    private sealed class DictionaryOptions
    {
        public Dictionary<string, string> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = "default-value"
        };
    }

    [Configuration("Alias", Kind = ConfigurationKind.Startup)]
    private sealed class AliasOptions
    {
        [ConfigurationKeyName("ConfiguredItems")]
        public List<string> Items { get; set; } = ["default"];
    }

    [Configuration("Runtime")]
    private sealed class RuntimeOptions
    {
        public List<string> Items { get; set; } = ["default"];
    }
}
