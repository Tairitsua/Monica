using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Monica.Configuration.Annotations;
using Monica.Configuration.Binding;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Xunit;

namespace Test.Monica.Configuration.Binding;

public sealed class ConfigurationObjectMaterializerTests
{
    [Theory]
    [InlineData("{}", "default", 1, false)]
    [InlineData("{\"entries\":[],\"Map\":{}}", null, 0, false)]
    [InlineData("{\"entries\":null,\"Map\":null}", null, -1, true)]
    public void Materialize_WhenContainersAreMissingEmptyOrNull_ShouldPreserveEachDistinctMeaning(string json,
        string? expectedItem, int expectedMapCount, bool expectedNull)
    {
        var (definition, materializer) = CreateMaterializer();
        using var document = JsonDocument.Parse(json);
        var projection = ConfigurationValueProjectionFactory.Create(definition.SectionPath, document.RootElement);
        var configuration = new ConfigurationBuilder().Add(new ConfigurationValueProjectionSource(projection)).Build();
        try
        {
            var options = (ShapeOptions)materializer.Materialize(definition, configuration);
            if (expectedNull) { options.Items.Should().BeNull(); options.Map.Should().BeNull(); }
            else
            {
                options.Items.Should().Equal(expectedItem is null ? [] : new[] { expectedItem });
                options.Map.Should().HaveCount(expectedMapCount);
            }
        }
        finally { (configuration as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void Materialize_WhenNewCollectionItemsHaveDefaults_ShouldReplaceTheirConfiguredCollectionsAndHonorAliases()
    {
        var (definition, materializer) = CreateMaterializer();
        var projection = materializer.Project(definition,
            """{"Children":[{"Items":["configured"]}],"entries":["owned"],"serializerName":["ignored"]}""");
        var configuration = new ConfigurationBuilder().Add(new ConfigurationValueProjectionSource(projection)).Build();
        try
        {
            var options = (ShapeOptions)materializer.Materialize(definition, configuration);
            options.Children.Single().Items.Should().Equal("configured");
            options.Items.Should().Equal("owned");
            materializer.Snapshot(definition, options).Should().Contain("\"entries\"").And.NotContain("serializerName");
        }
        finally { (configuration as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void Materialize_WhenProvidersOverlayNonemptyIndexesOrExplicitEmpty_ShouldMatchProductionBinding()
    {
        var (definition, materializer) = CreateMaterializer();
        var lower = materializer.Project(definition, """{"entries":["lower-0","lower-1"]}""");
        var higher = materializer.Project(definition, """{"entries":["higher-0"]}""");
        var empty = materializer.Project(definition, """{"entries":[]}""");
        var configuration = new ConfigurationBuilder().Add(new ConfigurationValueProjectionSource(lower))
            .Add(new ConfigurationValueProjectionSource(higher)).Build();
        try
        {
            var actual = new ShapeOptions();
            MonicaConfigurationBinder.Bind(configuration, definition.SectionPath, actual);
            var candidate = (ShapeOptions)materializer.Materialize(definition, configuration);
            actual.Items.Should().Equal("higher-0", "lower-1");
            candidate.Items.Should().Equal(actual.Items!);
        }
        finally { (configuration as IDisposable)?.Dispose(); }
        var emptyConfiguration = new ConfigurationBuilder().Add(new ConfigurationValueProjectionSource(lower))
            .Add(new ConfigurationValueProjectionSource(empty)).Build();
        try { ((ShapeOptions)materializer.Materialize(definition, emptyConfiguration)).Items.Should().BeEmpty(); }
        finally { (emptyConfiguration as IDisposable)?.Dispose(); }
    }

    private static (ConfigurationDefinition Definition, ConfigurationObjectMaterializer Materializer) CreateMaterializer()
    {
        var definition = new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(ShapeOptions));
        var registry = ConfigurationValidationTestServices.CreateLocalRegistry(typeof(ShapeOptions));
        return (definition, new ConfigurationObjectMaterializer(registry));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"scalar\"")]
    public void Materialize_WhenTheAggregateShapeIsInvalid_ShouldRejectItInsteadOfReturningDefaults(string json)
    {
        var (definition, materializer) = CreateMaterializer();
        using var document = JsonDocument.Parse(json);
        var configuration = new ConfigurationBuilder().Add(new ConfigurationValueProjectionSource(
            ConfigurationValueProjectionFactory.Create(definition.SectionPath, document.RootElement))).Build();
        try
        {
            var act = () => materializer.Materialize(definition, configuration);
            act.Should().Throw<ConfigurationValidationExecutionException>().Which.Kind.Should().Be(ConfigurationValidationIssueKind.Contract);
            materializer.ReadConfigurationJson(definition, configuration).Should().Be(json);
        }
        finally { (configuration as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void Project_WhenKnownPropertyNamesCollideByCase_ShouldRejectAmbiguityBeforePruningHistoricalFields()
    {
        var (definition, materializer) = CreateMaterializer();
        var act = () => materializer.Project(definition, """{"entries":["first"],"ENTRIES":["second"]}""");
        act.Should().Throw<ConfigurationValidationExecutionException>().Which.Stage.Should().Be("ambiguous-value-projection");
    }

    [Fact]
    public void Materialize_WhenAHigherAncestorIsNull_ShouldHideLowerDescendantsWithoutRestoringDefaultsBelowIt()
    {
        var (definition, materializer) = CreateMaterializer();
        var configuration = new ConfigurationBuilder().Add(new ConfigurationValueProjectionSource(
                materializer.Project(definition, """{"Children":[{"Items":["lower"]}]}""")))
            .Add(new ConfigurationValueProjectionSource(materializer.Project(definition, """{"Children":null}"""))).Build();
        try
        {
            var view = new ConfigurationShapeView(configuration);
            view.TryGetShape($"{definition.SectionPath}:Children:0:Items", out _).Should().BeFalse();
            view.GetSection($"{definition.SectionPath}:Children").GetChildren().Should().BeEmpty();
            ((ShapeOptions)materializer.Materialize(definition, configuration)).Children.Should().BeNull();
        }
        finally { (configuration as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void Materialize_WhenFlatSetReplacesAContainerThenAddsAnItem_ShouldUseTheCurrentShape()
    {
        var (definition, materializer) = CreateMaterializer();
        var configuration = new ConfigurationBuilder().Add(new ConfigurationValueProjectionSource(
            materializer.Project(definition, """{"entries":["old-0","old-1"]}"""))).Build();
        try
        {
            configuration[$"{definition.SectionPath}:entries"] = null;
            ((ShapeOptions)materializer.Materialize(definition, configuration)).Items.Should().BeNull();
            configuration[$"{definition.SectionPath}:entries:0"] = "new";
            ((ShapeOptions)materializer.Materialize(definition, configuration)).Items.Should().Equal("new");
        }
        finally { (configuration as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void ValidateConfiguration_WhenBindingNormalizesDuplicateSetItems_ShouldRetainTheSourceViolation()
    {
        var definition = new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(SetOptions));
        using var document = JsonDocument.Parse("""{"Items":["same","same"]}""");
        var configuration = new ConfigurationBuilder().Add(new ConfigurationValueProjectionSource(
            ConfigurationValueProjectionFactory.Create(definition.SectionPath, document.RootElement))).Build();
        try
        {
            var result = ConfigurationValidationTestServices.CreateCoordinator(typeof(SetOptions)).ValidateConfiguration(definition, configuration);
            result.IsValid.Should().BeFalse();
            result.Issues.Should().Contain(issue => issue.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        }
        finally { (configuration as IDisposable)?.Dispose(); }
    }

    [Configuration("ObjectShape")]
    private sealed class ShapeOptions
    {
        [ConfigurationKeyName("entries"), JsonPropertyName("serializerName")]
        public List<string>? Items { get; set; } = ["default"];
        public Dictionary<string, string>? Map { get; set; } = new() { ["default"] = "default" };
        public List<ChildOptions> Children { get; set; } = [];
    }
    private sealed class ChildOptions { public List<string> Items { get; set; } = ["default"]; }
    [Configuration("ObjectSet")]
    private sealed class SetOptions { public HashSet<string> Items { get; set; } = []; }
}
