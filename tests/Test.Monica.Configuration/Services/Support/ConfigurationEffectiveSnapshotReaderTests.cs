using Monica.Core.Configuration;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Annotations;
using Monica.Configuration.Binding;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Modules;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services.Support;

public sealed class ConfigurationEffectiveSnapshotReaderTests
{
    [Fact]
    public async Task CreateRequestAsync_WhenHigherProviderLeavesAnEmptyOwner_ShouldCaptureCompleteDefaultsWithoutLowerValues()
    {
        using var fixture = new SnapshotFixture(
            """{"retry-floor":99,"retry-ceiling":999,"Mode":"lower","entries":["lower"]}""", "{}");
        var actualOptions = new SnapshotOptions();
        MonicaConfigurationBinder.Bind(fixture.Configuration, fixture.Definition.SectionPath, actualOptions);
        var registry = new ConfigurationDefinitionRegistry();
        registry.RegisterRange([fixture.Definition]);
        var filter = Substitute.For<IConfigurationUnifiedVersionFilter>();
        filter.ShouldInclude(fixture.Definition).Returns(true);
        var factory = new ConfigurationUnifiedVersionSnapshotFactory(
            new ConfigurationDefinitionResolver(registry), fixture.Reader,
            Substitute.For<IConfigurationSourceInspector>(), fixture.Context, [filter],
            Options.Create(new ModuleConfigurationOption
            {
                UnifiedVersionControl = { Enabled = true }
            }));

        var request = await factory.CreateRequestAsync([fixture.Definition.DefinitionKey], "actual-group",
            new ConfigurationMutationContext(), DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        request.Should().NotBeNull();
        var capture = request!.Definitions.Should().ContainSingle().Which;
        using var json = JsonDocument.Parse(capture.Json);
        json.RootElement.GetProperty("retry-floor").GetInt32().Should().Be(actualOptions.Minimum);
        json.RootElement.GetProperty("retry-ceiling").GetInt32().Should().Be(actualOptions.Maximum);
        json.RootElement.GetProperty("Mode").GetString().Should().Be(actualOptions.Mode);
        actualOptions.Minimum.Should().Be(12);
        actualOptions.Maximum.Should().Be(60);
        actualOptions.Mode.Should().Be("Balanced");
        actualOptions.Items.Should().Equal("default");
        json.RootElement.TryGetProperty("serializerFloor", out _).Should().BeFalse();
        capture.EffectiveValueVersion.Should().Be(7);
        var validator = ConfigurationValidationTestServices.CreateCoordinator(typeof(SnapshotOptions));
        validator.ValidateCompleteValue(fixture.Definition, capture.Json, ConfigurationValidationProfile.CapturedValue)
            .IsValid.Should().BeTrue();
        validator.ValidateCompleteValue(fixture.Definition, "{}", ConfigurationValidationProfile.CapturedValue)
            .IsValid.Should().BeFalse();
        fixture.Seeds.CreateRuntimeJson(fixture.Definition.Root, fixture.Definition.SectionPath).Should().Be("{}");
    }

    [Fact]
    public async Task ReadAsync_WhenOnlySomeAliasesAreConfigured_ShouldCompleteAbsentMembersAndKeepConfiguredValues()
    {
        using var fixture = new SnapshotFixture("""{"retry-floor":21,"serializerFloor":999}""");

        var snapshot = await fixture.Reader.ReadAsync(fixture.Definition, TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(snapshot.Json);
        json.RootElement.GetProperty("retry-floor").GetInt32().Should().Be(21);
        json.RootElement.GetProperty("retry-ceiling").GetInt32().Should().Be(60);
        json.RootElement.GetProperty("Mode").GetString().Should().Be("Balanced");
        json.RootElement.TryGetProperty("serializerFloor", out _).Should().BeFalse();
        snapshot.Version.Should().Be(7);
        snapshot.PersistedDocument.Should().BeSameAs(fixture.Documents[fixture.Definition.DefinitionKey]);
    }

    [Theory]
    [InlineData("{}", 1, 1, false)]
    [InlineData("{\"entries\":[],\"Map\":{}}", 0, 0, false)]
    [InlineData("{\"entries\":null,\"Map\":null}", 0, 0, true)]
    public async Task ReadAsync_WhenCollectionsAreAbsentEmptyOrNull_ShouldPreserveProductionBinding(
        string configuredJson, int itemCount, int mapCount, bool isNull)
    {
        using var fixture = new SnapshotFixture(configuredJson);

        var snapshot = await fixture.Reader.ReadAsync(fixture.Definition, TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(snapshot.Json);
        var items = json.RootElement.GetProperty("entries");
        var map = json.RootElement.GetProperty("Map");
        if (isNull)
        {
            items.ValueKind.Should().Be(JsonValueKind.Null);
            map.ValueKind.Should().Be(JsonValueKind.Null);
        }
        else
        {
            items.GetArrayLength().Should().Be(itemCount);
            map.EnumerateObject().Count().Should().Be(mapCount);
        }
    }

    [Fact]
    public async Task ReadManyAsync_WhenLocalAndPublishedOwnersAreMixed_ShouldPreserveOrderAndPublishedBytes()
    {
        using var fixture = new SnapshotFixture("{}");
        var published = fixture.Definition with
        {
            DefinitionKey = "published-only", Origin = ConfigurationDefinitionOrigin.PublishedMetadata,
            ClrTypeName = "Unavailable.Remote.Type"
        };
        const string publishedJson = "{ \"retry-floor\" : 42, \"unknown-owner-field\" : true }";
        fixture.Documents[published.DefinitionKey] = new ConfigurationEffectiveValueDocument
        {
            DefinitionKey = published.DefinitionKey, Json = publishedJson, Version = 31, SchemaVersion = 1
        };

        var snapshots = await fixture.Reader.ReadManyAsync([published, fixture.Definition],
            TestContext.Current.CancellationToken);

        snapshots.Should().HaveCount(2);
        snapshots[0].Json.Should().Be(publishedJson);
        snapshots[0].Version.Should().Be(31);
        using var localJson = JsonDocument.Parse(snapshots[1].Json);
        localJson.RootElement.GetProperty("retry-floor").GetInt32().Should().Be(12);
        await fixture.Store.Received(1).GetManyAsync(
            Arg.Is<IReadOnlyList<string>>(keys => keys.SequenceEqual(new[] { "published-only", fixture.Definition.DefinitionKey })),
            TestContext.Current.CancellationToken);
        await fixture.Store.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadAsync_WhenPublishedDocumentIsMissing_ShouldFailWithoutExecutingLocalDefaults()
    {
        using var fixture = new SnapshotFixture("{}");
        var published = fixture.Definition with
        {
            DefinitionKey = "missing-published", Origin = ConfigurationDefinitionOrigin.PublishedMetadata
        };

        var action = () => fixture.Reader.ReadAsync(published, TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void CreateEffectiveJson_WhenLocalAuthorityIsMissing_ShouldRejectInsteadOfFallingBackToASourceProjection()
    {
        using var fixture = new SnapshotFixture("{}");
        var seedsWithoutAuthority = ConfigurationValidationTestServices.CreateSeedFactory(fixture.Context);

        var action = () => seedsWithoutAuthority.CreateEffectiveJson(fixture.Definition);

        action.Should().Throw<ConfigurationValidationExecutionException>();
    }

    private sealed class SnapshotFixture : IDisposable
    {
        public SnapshotFixture(params string[] providerJson)
        {
            Definition = new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(SnapshotOptions));
            var builder = new ConfigurationBuilder();
            foreach (var json in providerJson)
            {
                using var document = JsonDocument.Parse(json);
                builder.Add(new ConfigurationValueProjectionSource(
                    ConfigurationValueProjectionFactory.Create(Definition.SectionPath, document.RootElement)));
            }
            Configuration = builder.Build();
            Context = new ConfigurationRuntimeContext();
            Context.Capture(Configuration);
            Seeds = ConfigurationValidationTestServices.CreateSeedFactory(Context, typeof(SnapshotOptions));
            Documents[Definition.DefinitionKey] = new ConfigurationEffectiveValueDocument
            {
                DefinitionKey = Definition.DefinitionKey, Json = "{}", Version = 20, SchemaVersion = Definition.SchemaVersion
            };
            Store = Substitute.For<IConfigurationEffectiveValueStore>();
            Store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Documents.GetValueOrDefault(call.ArgAt<string>(0)));
            Store.GetManyAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.ArgAt<IReadOnlyList<string>>(0).Select(key => Documents.GetValueOrDefault(key)).ToArray());
            Reader = new ConfigurationEffectiveSnapshotReader(Store, Seeds,
                new RecordingReloadCoordinator(Definition.DefinitionKey));
        }

        public ConfigurationDefinition Definition { get; }
        public IConfigurationRoot Configuration { get; }
        public ConfigurationRuntimeContext Context { get; }
        public ConfigurationEffectiveValueSeedFactory Seeds { get; }
        public IConfigurationEffectiveValueStore Store { get; }
        public ConfigurationEffectiveSnapshotReader Reader { get; }
        public Dictionary<string, ConfigurationEffectiveValueDocument> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Dispose() => (Configuration as IDisposable)?.Dispose();
    }

    private sealed class RecordingReloadCoordinator(string localDefinitionKey) : IConfigurationReloadCoordinator
    {
        public List<string> VersionQueries { get; } = [];

        public long? GetLoadedMonicaProjectionVersion(string definitionKey)
        {
            VersionQueries.Add(definitionKey);
            return string.Equals(definitionKey, localDefinitionKey, StringComparison.Ordinal) ? 7 : null;
        }

        public Task ReloadMonicaProjectionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReloadMonicaProjectionAsync(string definitionKey, long? minimumVersion,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReloadRuntimeConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Configuration("EffectiveSnapshot")]
    private sealed class SnapshotOptions
    {
        public SnapshotOptions() { }

        [ConfigurationKeyName("retry-floor"), JsonPropertyName("serializerFloor")]
        public int Minimum { get; set; } = 12;
        [ConfigurationKeyName("retry-ceiling")]
        public int Maximum { get; set; } = 60;
        public string Mode { get; set; } = "Balanced";
        [ConfigurationKeyName("entries")]
        public List<string>? Items { get; set; } = ["default"];
        public Dictionary<string, string>? Map { get; set; } = new() { ["default"] = "default" };
    }
}
