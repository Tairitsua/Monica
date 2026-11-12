using Monica.Core.Configuration;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Monica.Configuration.Annotations;
using Monica.Configuration.Binding;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Monica.Configuration.Serialization;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Configuration.Services.Support;

public sealed class ConfigurationRollbackPersistencePlannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"monica-rollback-barrier-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("{\"Planner\":{}}")]
    [InlineData("{\"Planner\":{\"Child\":{}}}")]
    [InlineData("{\"Planner\":{\"Child\":null}}")]
    public async Task FindRuntimeSourceDriftsAsync_WhenEmptyOrNullProviderHidesLowerValues_ShouldAcceptBoundDefaults(
        string upperJson)
    {
        await WriteInputsAsync(upperJson);
        using var fixture = CreateFixture();

        var drifts = await fixture.Planner.FindRuntimeSourceDriftsAsync(fixture.Definition,
            fixture.Current, TestContext.Current.CancellationToken);

        drifts.Should().BeEmpty();
        fixture.Inspector.GetSourceChain(fixture.Definition, LogicalPath.FromProperties("Child", "Count"))
            .Values.Should().ContainSingle().Which.IsEffective.Should().BeFalse();
    }

    [Fact]
    public async Task FindRuntimeSourceDriftsAsync_WhenMaskedPhysicalSourceChangesWithoutReload_ShouldStillReportProjectionDrift()
    {
        await WriteInputsAsync("{\"Planner\":{}}");
        using var fixture = CreateFixture();
        var before = await fixture.Planner.FindRuntimeSourceDriftsAsync(fixture.Definition,
            fixture.Current, TestContext.Current.CancellationToken);
        before.Should().BeEmpty();
        await File.WriteAllTextAsync(Path.Combine(_directory, "lower.json"),
            "{\"Planner\":{\"retry-floor\":72,\"Child\":{\"Count\":81}}}", TestContext.Current.CancellationToken);

        var drifts = await fixture.Planner.FindRuntimeSourceDriftsAsync(fixture.Definition,
            fixture.Current, TestContext.Current.CancellationToken);

        var drift = drifts.Should().ContainSingle().Which;
        drift.Status.Should().Be(ConfigurationUnifiedVersionApplyMutationStatus.RuntimeOutOfSync);
        drift.SourceKey.Should().Be(fixture.Inspector.GetSources().Single(source => source.PriorityIndex == 0).SourceKey);
        drift.LogicalPath.Should().Be(LogicalPath.Root.ToCanonicalString());
        drift.ExpectedSourceRevision.Should().NotBeNullOrEmpty();
    }

    private async Task WriteInputsAsync(string upperJson)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "lower.json"),
            "{\"Planner\":{\"retry-floor\":71,\"Child\":{\"Count\":81}}}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_directory, "upper.json"), upperJson, TestContext.Current.CancellationToken);
    }

    private PlannerFixture CreateFixture()
    {
        var configuration = new ConfigurationBuilder().SetBasePath(_directory)
            .AddShapeAwareJsonFile("lower.json", optional: false, reloadOnChange: false)
            .AddShapeAwareJsonFile("upper.json", optional: false, reloadOnChange: false)
            .Build();
        var definition = new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(PlannerOptions));
        var context = new ConfigurationRuntimeContext();
        context.Capture(configuration);
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(definition);
        var inspector = new ConfigurationSourceInspector(context, registry, new ConfigurationPathProjector(),
            Options.Create(new ModuleConfigurationOption { IncludeUnmanagedSourceInventoryItems = false }));
        var seeds = ConfigurationValidationTestServices.CreateSeedFactory(context, typeof(PlannerOptions));
        var current = new ConfigurationResolvedEffectiveSnapshot(seeds.CreateEffectiveJson(definition), null, null);
        var planner = new ConfigurationRollbackPersistencePlanner(inspector, new ConfigurationJsonFileSourceWriter(),
            new ConfigurationEffectiveValueDocumentEditor(new ConfigurationEffectiveValuePatchEngine(), new ConfigurationStoredValueCodec()),
            new ConfigurationPathProjector(), new MonicaConfigurationProviderAccessor());
        return new PlannerFixture(configuration, definition, current, inspector, planner);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed record PlannerFixture(IConfigurationRoot Configuration, ConfigurationDefinition Definition,
        ConfigurationResolvedEffectiveSnapshot Current, ConfigurationSourceInspector Inspector,
        ConfigurationRollbackPersistencePlanner Planner) : IDisposable
    {
        public void Dispose() => (Configuration as IDisposable)?.Dispose();
    }

    [Configuration("Planner")]
    private sealed class PlannerOptions
    {
        public PlannerOptions() { }
        [ConfigurationKeyName("retry-floor")]
        public int Minimum { get; set; } = 7;
        public PlannerChild? Child { get; set; } = new();
    }

    private sealed class PlannerChild
    {
        public int Count { get; set; } = 3;
    }
}
