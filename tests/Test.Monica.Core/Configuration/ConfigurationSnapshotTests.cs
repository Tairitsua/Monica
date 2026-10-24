using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Monica.Core.Configuration;
using Xunit;

namespace Test.Monica.Core.Configuration;

public sealed class ConfigurationSnapshotTests
{
    [Fact]
    public void Capture_WhenValuesReload_ShouldKeepOriginalContributionsAndShapes()
    {
        using var configuration = CreateConfiguration();
        AddJson(configuration, """{"Settings":{"Items":["lower","tail"],"Map":{"old":1},"Nullable":{"old":1}}}""");
        AddJson(configuration, """{"Settings":{"Items":["higher"],"Map":{},"Nullable":null}}""");

        var snapshot = ConfigurationSnapshot.Capture(configuration);
        var view = new ConfigurationShapeView(snapshot);
        snapshot["Settings:Items:0"].Should().Be("higher");
        snapshot["Settings:Items:1"].Should().Be("tail");
        snapshot.GetSection("Settings:Map").GetChildren().Should().BeEmpty();
        snapshot["Settings:Nullable:old"].Should().BeNull();
        view.TryGetShape("Settings:Map", out var map).Should().BeTrue();
        map.Should().Be(ConfigurationValueShape.Object);
        view.TryGetShape("Settings:Nullable", out var nullable).Should().BeTrue();
        nullable.Should().Be(ConfigurationValueShape.Null);

        configuration["Settings:Items:0"] = "changed";
        configuration["Settings:Map:new"] = "new";
        snapshot["Settings:Items:0"].Should().Be("higher");
        snapshot.GetSection("Settings:Map").GetChildren().Should().BeEmpty();
        snapshot.Contributions[0].Values["Settings:Items:0"].Should().Be("lower");
        snapshot.Contributions[1].Values["Settings:Items:0"].Should().Be("higher");
        Action changeRoot = () => snapshot["Settings:Items:0"] = "forbidden";
        changeRoot.Should().Throw<NotSupportedException>();
        Action changeProvider = () => snapshot.Contributions[0].Set("Settings:Items:0", "forbidden");
        changeProvider.Should().Throw<NotSupportedException>();
    }

    [Theory]
    [InlineData("{\"Application\":{\"Deployments\":{\"local\":{\"Roles\":[\"one\"]}}}}", ConfigurationValueShape.Object)]
    [InlineData("{\"Application\":{\"Deployments\":{}}}", ConfigurationValueShape.Object)]
    [InlineData("{\"Application\":{\"Deployments\":null}}", ConfigurationValueShape.Null)]
    public void SelectCompleteSection_ShouldReadOnlyTheSelectedProvider(string higherJson, ConfigurationValueShape expected)
    {
        using var configuration = CreateConfiguration();
        AddJson(configuration, """{"Application":{"Deployments":{"remote":{"Roles":["one","two"]}}}}""");
        AddJson(configuration, higherJson);

        var snapshot = ConfigurationSnapshot.Capture(configuration);
        var selected = snapshot.SelectCompleteSection("Application:Deployments");
        selected.Should().NotBeNull();
        selected!.Origin.PriorityIndex.Should().Be(1);
        selected.TryGetShape("Application:Deployments", out var shape).Should().BeTrue();
        shape.Should().Be(expected);
        selected.GetSection("Application:Deployments:remote").GetChildren().Should().BeEmpty();
        selected.GetSection("Application:Deployments:local:Roles:1").Value.Should().BeNull();
    }

    [Theory]
    [InlineData("{\"Application\":null}")]
    [InlineData("{\"Application\":{}}")]
    public void SelectCompleteSection_WhenAnAncestorMasksTheSection_ShouldNotFallBack(string higherJson)
    {
        using var configuration = CreateConfiguration();
        AddJson(configuration, """{"Application":{"EnvironmentFiles":["lower.json"]}}""");
        AddJson(configuration, higherJson);
        var selected = ConfigurationSnapshot.Capture(configuration).SelectCompleteSection("Application:EnvironmentFiles");
        selected.Should().NotBeNull();
        selected!.Origin.PriorityIndex.Should().Be(1);
        selected.GetSection("Application:EnvironmentFiles").GetChildren().Should().BeEmpty();
    }

    [Fact]
    public void Capture_WhenProvidersAreChained_ShouldKeepLeafOriginsAndPriority()
    {
        using var inner = CreateConfiguration();
        AddJson(inner, """{"Settings":{"Items":["first","second"]}}""");
        AddJson(inner, """{"Settings":{"Empty":[],"Text":"inner"}}""");
        using var outer = CreateConfiguration();
        outer.AddConfiguration(inner);
        outer.AddInMemoryCollection(new Dictionary<string, string?> { ["Settings:Text"] = "outer" });

        var snapshot = ConfigurationSnapshot.Capture(outer);
        snapshot.Contributions.Should().HaveCount(3);
        snapshot.Contributions.Select(provider => provider.Origin.ProviderPath).Should().Equal("0/0", "0/1", "1");
        snapshot.Contributions.Select(provider => provider.Origin.PriorityIndex).Should().Equal(0, 1, 2);
        snapshot["Settings:Text"].Should().Be("outer");
        new ConfigurationShapeView(snapshot).TryGetShape("Settings:Empty", out var shape).Should().BeTrue();
        shape.Should().Be(ConfigurationValueShape.List);
        inner["Settings:Text"] = "later";
        snapshot.Contributions[1].GetSection("Settings:Text").Value.Should().Be("inner");
    }

    [Fact]
    public void Capture_WhenAChainIsCyclic_ShouldFailWithoutReadingValues()
    {
        var root = new CyclicRoot();
        Action capture = () => ConfigurationSnapshot.Capture(root);
        capture.Should().Throw<InvalidOperationException>().WithMessage("*acyclic*");
    }

    [Fact]
    public void Capture_WhenAnExistingSnapshotIsChained_ShouldRebasePositionsAndKeepSourceIdentity()
    {
        using var original = CreateConfiguration();
        AddJson(original, """{"Settings":{"Value":"snapshot"}}""");
        var captured = ConfigurationSnapshot.Capture(original);
        using var root = CreateConfiguration();
        root.AddInMemoryCollection(new Dictionary<string, string?> { ["Settings:Value"] = "lower" });
        root.AddConfiguration(captured);
        var nested = ConfigurationSnapshot.Capture(root);
        nested.Contributions.Select(provider => provider.Origin.PriorityIndex).Should().Equal(0, 1);
        nested.Contributions.Select(provider => provider.Origin.ProviderPath).Should().Equal("0", "1/0");
        nested.Contributions[1].Origin.ProviderType.Should().Be(captured.Contributions[0].Origin.ProviderType);
        nested.Contributions[1].Origin.SourcePath.Should().Be(captured.Contributions[0].Origin.SourcePath);
        nested["Settings:Value"].Should().Be("snapshot");
    }

    [Fact]
    public void GetOrigin_WhenPhysicalPathsAreUnavailable_ShouldKeepSafeSourceIdentity()
    {
        using var provider = new JsonConfigurationProvider(new JsonConfigurationSource
        {
            Path = "custom.json", FileProvider = new UnavailableFileProvider(), Optional = true, ReloadOnChange = false
        });
        var origin = ConfigurationSnapshot.GetOrigin(provider, 3, "1/3");
        origin.SourcePath.Should().Be("custom.json");
        origin.PhysicalPath.Should().BeNull();
        origin.ProviderPath.Should().Be("1/3");
    }

    private static void AddJson(IConfigurationBuilder builder, string json)
    {
        using var document = JsonDocument.Parse(json);
        builder.Add(new ConfigurationValueProjectionSource(ConfigurationValueProjectionFactory.CreateDocument(document.RootElement)));
    }

    private static ConfigurationManager CreateConfiguration()
    {
        var configuration = new ConfigurationManager();
        configuration.Sources.Clear();
        return configuration;
    }

    private sealed class UnavailableFileProvider : IFileProvider
    {
        public IFileInfo GetFileInfo(string subpath) => throw new NotSupportedException("No physical paths.");
        public IDirectoryContents GetDirectoryContents(string subpath) => throw new NotSupportedException();
        public IChangeToken Watch(string filter) => new CancellationChangeToken(CancellationToken.None);
    }

    private sealed class CyclicRoot : IConfigurationRoot
    {
        public IEnumerable<IConfigurationProvider> Providers =>
            [new ChainedConfigurationProvider(new ChainedConfigurationSource { Configuration = this })];
        public string? this[string key] { get => throw new InvalidOperationException(); set => throw new InvalidOperationException(); }
        public IConfigurationSection GetSection(string key) => throw new InvalidOperationException();
        public IEnumerable<IConfigurationSection> GetChildren() => throw new InvalidOperationException();
        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
        public void Reload() => throw new InvalidOperationException();
    }
}
