using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Monica.Configuration.Binding;
using Xunit;

namespace Test.Monica.Configuration.Binding;

public sealed class ConfigurationDocumentProjectionTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Enabled\":true,\"Disabled\":false,\"Text\":\"escaped\\nvalue\",\"Number\":1.25e2,\"EmptyObject\":{},\"EmptyList\":[],\"Null\":null,\"Values\":[true,3]}")]
    public void CreateDocument_WhenComparedToTheRealJsonProvider_ShouldPreserveContributionTextAndShape(string json)
    {
        using var document = JsonDocument.Parse(json);
        var projection = ConfigurationValueProjectionFactory.CreateDocument(document.RootElement);
        using var actual = new ShapeAwareJsonConfigurationProvider(new JsonConfigurationSource());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        actual.Load(stream);
        var projected = new ConfigurationValueProjectionProvider(projection);
        projected.Load();
        var paths = projection.Values.Keys.Concat(projection.Shapes.Keys).Append(string.Empty).Distinct().ToArray();
        foreach (var path in paths)
        {
            var expectedPresent = actual.TryGet(path, out var expectedValue);
            projected.TryGet(path, out var value).Should().Be(expectedPresent, $"contribution at '{path}'");
            value.Should().Be(expectedValue);
            var expectedShapePresent = actual.TryGetShape(path, out var expectedShape);
            projected.TryGetShape(path, out var shape).Should().Be(expectedShapePresent, $"shape at '{path}'");
            if (expectedShapePresent) shape.Should().Be(expectedShape);
        }
    }

    [Fact]
    public void CreateDocument_WhenTheHigherWholeFileIsEmpty_ShouldPreserveLowerProviderValues()
    {
        using var lowerJson = JsonDocument.Parse("""{"Settings":{"Values":["lower"]}}""");
        using var higherJson = JsonDocument.Parse("{}");
        var configuration = new ConfigurationBuilder()
            .Add(new ConfigurationValueProjectionSource(ConfigurationValueProjectionFactory.CreateDocument(lowerJson.RootElement)))
            .Add(new ConfigurationValueProjectionSource(ConfigurationValueProjectionFactory.CreateDocument(higherJson.RootElement)))
            .Build();
        try
        {
            var view = new ConfigurationShapeView(configuration);
            view.GetSection("Settings:Values:0").Value.Should().Be("lower");
            view.GetSection("Settings:Values").GetChildren().Should().ContainSingle();
        }
        finally { (configuration as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void ProjectionProvider_WhenReloadedAfterSet_ShouldRestoreBothOriginalValuesAndShapes()
    {
        using var document = JsonDocument.Parse("""{"Settings":{"Values":["original"]}}""");
        var provider = new ConfigurationValueProjectionProvider(ConfigurationValueProjectionFactory.CreateDocument(document.RootElement));
        provider.Load();
        provider.Set("Settings:Values", null);
        provider.Load();
        provider.TryGetShape("Settings:Values", out var shape).Should().BeTrue();
        shape.Should().Be(ConfigurationValueShape.List);
        provider.TryGet("Settings:Values:0", out var value).Should().BeTrue();
        value.Should().Be("original");
    }

    [Theory]
    [InlineData("{\"A:B\":1,\"A\":{\"B\":1}}")]
    [InlineData("{\"A:B\":1,\"a\":{\"b\":2}}")]
    [InlineData("{\"A\":{},\"a\":null}")]
    public void CreateDocument_WhenJsonPathsFlattenToTheSameKey_ShouldRejectLikeTheRealJsonProvider(string json)
    {
        using var document = JsonDocument.Parse(json);
        var act = () => ConfigurationValueProjectionFactory.CreateDocument(document.RootElement);
        act.Should().Throw<FormatException>().Which.InnerException.Should().BeNull();
        using var provider = new ShapeAwareJsonConfigurationProvider(new JsonConfigurationSource());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var actualLoad = () => provider.Load(stream);
        actualLoad.Should().Throw<FormatException>();
    }

    [Fact]
    public void PreserveJsonShapes_WhenLoadedProvidersContainProgrammaticValues_ShouldRetainTheirPriorityAndReloadLifecycle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"monica-json-projection-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"Settings":{"Text":"file","Enabled":false,"Values":["file-item"]}}""");
        try
        {
            using var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Settings:Text"] = "lower" });
            configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
            configuration["Settings:Text"] = "programmatic";
            configuration["Settings:Enabled"] = "true";
            configuration["Settings:Values"] = null;
            var before = ((IConfigurationRoot)configuration).Providers.Count();

            ShapeAwareJsonConfigurationExtensions.PreserveJsonShapes(configuration);

            var providers = ((IConfigurationRoot)configuration).Providers.ToArray();
            providers.Should().HaveCount(before);
            providers[^1].Should().BeOfType<ShapeAwareJsonConfigurationProvider>();
            foreach (var provider in providers)
            {
                provider.TryGet("Settings:Text", out var value).Should().BeTrue();
                value.Should().Be("programmatic");
            }
            configuration["Settings:Enabled"].Should().Be("true");
            var view = new ConfigurationShapeView(configuration);
            view.GetSection("Settings:Values").GetChildren().Should().BeEmpty();
            view.TryGetShape("Settings:Values:0", out _).Should().BeFalse();

            providers[^1].Load();
            configuration["Settings:Text"].Should().Be("file");
            configuration["Settings:Enabled"].Should().Be("False");
            view.GetSection("Settings:Values:0").Value.Should().Be("file-item");
        }
        finally { File.Delete(path); }
    }
}
