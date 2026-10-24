using Monica.Core.Configuration;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Annotations;
using Monica.Configuration.Binding;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class ConfigurationSourceInspectorTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"monica-source-inspector-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadSources_WhenSensitiveDisclosureIsSelected_ShouldUseItForChainsInventoriesAndFiles(bool includeSensitive)
    {
        Directory.CreateDirectory(_rootDirectory);
        await File.WriteAllTextAsync(Path.Combine(_rootDirectory, "disclosure.json"),
            """{"Disclosure":{"Password":"synthetic-secret","ConsumerOriginator":"ordinary-value","Group":{"Token":"nested-secret"},"Secrets":["list-secret"]}}""",
            TestContext.Current.CancellationToken);
        var configuration = new ConfigurationBuilder().SetBasePath(_rootDirectory)
            .AddJsonFile("disclosure.json", optional: false, reloadOnChange: false).Build();
        using var configurationLifetime = configuration as IDisposable;
        var definition = new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(DisclosureOptions));
        var inspector = CreateInspector(configuration, definition);

        var chain = inspector.GetSourceChain(definition, LogicalPath.FromProperties("Password"), includeSensitive);
        var root = inspector.GetSourceChain(definition, LogicalPath.Root, includeSensitive);
        var inventory = (await inspector.GetSourceInventoriesAsync(TestContext.Current.CancellationToken, includeSensitive)).Single();
        var file = await inspector.GetSourceFileViewAsync(inspector.GetSources().Single().SourceKey,
            TestContext.Current.CancellationToken, includeSensitive);

        chain.Values.Single().IsSensitive.Should().BeTrue();
        chain.Values.Single().DisplayValue.Should().Be(includeSensitive ? "synthetic-secret" : null);
        inventory.Items.Single(item => item.ConfigurationPath == "Disclosure:Password").DisplayValue
            .Should().Be(includeSensitive ? "synthetic-secret" : null);
        inventory.Items.Single(item => item.ConfigurationPath == "Disclosure:ConsumerOriginator").DisplayValue
            .Should().Be("ordinary-value");
        file.HasRedactions.Should().Be(!includeSensitive);
        foreach (var json in new[] { root.Values.Single().DisplayValue!, file.Content })
        {
            json.Should().Contain("ordinary-value");
            foreach (var marker in new[] { "synthetic-secret", "nested-secret", "list-secret" })
            {
                if (includeSensitive) json.Should().Contain(marker);
                else json.Should().NotContain(marker);
            }
        }
    }

    [Configuration("Disclosure")]
    private sealed class DisclosureOptions
    {
        [OptionSetting(IsSensitive = true)] public string Password { get; set; } = string.Empty;
        public string ConsumerOriginator { get; set; } = string.Empty;
        public DisclosureGroup Group { get; set; } = new();
        [OptionSetting(IsSensitive = true)] public List<string> Secrets { get; set; } = [];
    }

    private sealed class DisclosureGroup
    {
        [OptionSetting(IsSensitive = true)] public string Token { get; set; } = string.Empty;
    }

    [Fact]
    public async Task GetSourceInventories_WhenJsonProvidersSharePhysicalFile_ShouldKeepProviderInstancesDistinct()
    {
        Directory.CreateDirectory(_rootDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(_rootDirectory, "appsettings.json"),
            """
            {
              "Test": {
                "App": {
                  "WorkerId": 7
                }
              }
            }
            """,
            TestContext.Current.CancellationToken);
        var configuration = new ConfigurationBuilder()
            .SetBasePath(_rootDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();
        var inspector = CreateInspector(configuration);

        var sources = inspector.GetSources();
        var inventories = await inspector.GetSourceInventoriesAsync(TestContext.Current.CancellationToken);

        sources.Should().HaveCount(2);
        sources.Select(source => source.SourceKey).Should().OnlyHaveUniqueItems();
        inventories.Should().HaveCount(2);
        inventories.Select(inventory => inventory.Source.SourceKey).Should().OnlyHaveUniqueItems();
        inventories.Select(inventory => inventory.Source.PriorityIndex).Should().Equal(1, 0);
        inventories.Should().AllSatisfy(inventory =>
        {
            inventory.SuppliedValueCount.Should().Be(1);
            inventory.Items.Should().ContainSingle(item => item.ConfigurationPath == "Test:App:WorkerId");
        });
        inventories.Single(inventory => inventory.Source.PriorityIndex == 1).EffectiveValueCount.Should().Be(1);
        inventories.Single(inventory => inventory.Source.PriorityIndex == 0).EffectiveValueCount.Should().Be(0);
    }

    [Theory]
    [InlineData("{}", "WorkerId", 0)]
    [InlineData("{\"Child\":{}}", "Child.Count", 3)]
    [InlineData("{\"Child\":null}", "Child.Count", 3)]
    public async Task GetSourceChains_WhenHigherAncestorIsEmptyOrNull_ShouldRetainLowerEvidenceWithoutAnEffectiveWinner(
        string higherJson, string logicalPath, int lowerEffectiveCount)
    {
        var definition = BarrierDefinition();
        var configuration = BuildShapeConfiguration(definition, higherJson);
        using var lifetime = configuration as IDisposable;
        var inspector = CreateInspector(configuration, definition);

        var chain = inspector.GetSourceChain(definition, LogicalPath.FromProperties(logicalPath.Split('.')));
        var contributions = inspector.GetDefinitionContributions(definition);
        var inventories = await inspector.GetSourceInventoriesAsync(TestContext.Current.CancellationToken);

        chain.Values.Should().ContainSingle().Which.IsEffective.Should().BeFalse();
        var lower = contributions.Single(contribution => contribution.Source.PriorityIndex == 0);
        lower.SuppliedValueCount.Should().Be(4);
        lower.EffectiveValueCount.Should().Be(lowerEffectiveCount);
        var lowerInventory = inventories.Single(inventory => inventory.Source.PriorityIndex == 0);
        lowerInventory.SuppliedValueCount.Should().Be(4);
        lowerInventory.EffectiveValueCount.Should().Be(lowerEffectiveCount);
        lowerInventory.Items.Single(item => item.ConfigurationPath == chain.ConfigurationPath)
            .IsEffective.Should().BeFalse();
    }

    [Theory]
    [InlineData("{}", "", "{}")]
    [InlineData("{\"Child\":{}}", "Child", "{}")]
    [InlineData("{\"Child\":null}", "Child", "null")]
    [InlineData("{\"entries\":[]}", "entries", "[]")]
    public void GetSourceChain_WhenUpperContainerIsExplicitlyEmptyOrNull_ShouldExposeItsActualWinningShape(
        string higherJson, string logicalPath, string expectedDisplay)
    {
        var definition = BarrierDefinition();
        var configuration = BuildShapeConfiguration(definition, higherJson);
        using var lifetime = configuration as IDisposable;
        var inspector = CreateInspector(configuration, definition);
        var path = logicalPath.Length == 0 ? LogicalPath.Root : LogicalPath.FromProperties(logicalPath);

        var chain = inspector.GetSourceChain(definition, path);

        chain.Values.Should().HaveCount(2);
        chain.Values[0].IsEffective.Should().BeTrue();
        chain.Values[0].Source.PriorityIndex.Should().Be(1);
        chain.Values[0].DisplayValue.Should().Be(expectedDisplay);
        chain.Values[1].IsEffective.Should().BeFalse();
    }

    [Fact]
    public void GetSourceChain_WhenUpperObjectIsNonempty_ShouldMergeUnspecifiedLowerMembers()
    {
        var definition = BarrierDefinition();
        var configuration = BuildShapeConfiguration(definition, """{"WorkerId":8}""");
        using var lifetime = configuration as IDisposable;
        var inspector = CreateInspector(configuration, definition);

        var lowerMember = inspector.GetSourceChain(definition, LogicalPath.FromProperties("Child", "Count"));
        var overriddenMember = inspector.GetSourceChain(definition, LogicalPath.FromProperties("WorkerId"));

        lowerMember.Values.Should().ContainSingle().Which.IsEffective.Should().BeTrue();
        lowerMember.Values[0].Source.PriorityIndex.Should().Be(0);
        overriddenMember.Values.Should().HaveCount(2);
        overriddenMember.Values[0].IsEffective.Should().BeTrue();
        overriddenMember.Values[0].Source.PriorityIndex.Should().Be(1);
        overriddenMember.Values[1].IsEffective.Should().BeFalse();
    }

    [Fact]
    public void GetSourceChain_WhenScalarIsExplicitNull_ShouldKeepItsUpperWinnerInsteadOfFallingBack()
    {
        var definition = BarrierDefinition();
        var configuration = BuildShapeConfiguration(definition, """{"Caption":null}""");
        using var lifetime = configuration as IDisposable;
        var inspector = CreateInspector(configuration, definition);

        var chain = inspector.GetSourceChain(definition, LogicalPath.FromProperties("Caption"));

        chain.Values.Should().HaveCount(2);
        chain.Values[0].IsEffective.Should().BeTrue();
        chain.Values[0].IsSensitive.Should().BeFalse();
        chain.Values[0].DisplayValue.Should().BeNull();
        chain.Values[1].IsEffective.Should().BeFalse();
    }

    [Theory]
    [InlineData("{}", "WorkerId", 0, false)]
    [InlineData("{\"Child\":{}}", "Child.Count", 3, false)]
    [InlineData("{\"Child\":null}", "Child.Count", 3, false)]
    [InlineData("{\"entries\":[]}", "entries[@0]", 3, false)]
    [InlineData("{}", "WorkerId", 0, true)]
    public async Task GetSourceEvidence_WhenIndependentChainMasksLowerValues_ShouldKeepPhysicalEvidenceWithoutAFalseWinner(
        string higherJson, string logicalPath, int lowerEffectiveCount, bool nested)
    {
        var definition = BarrierDefinition();
        var configuration = BuildChainedShapeConfiguration(definition, higherJson, nested);
        using var lifetime = configuration as IDisposable;
        var inspector = CreateInspector(configuration, definition);

        var chain = inspector.GetSourceChain(definition, LogicalPath.Parse(logicalPath));
        var contributions = inspector.GetDefinitionContributions(definition);
        var inventories = await inspector.GetSourceInventoriesAsync(TestContext.Current.CancellationToken);
        var options = new BarrierOptions();
        MonicaConfigurationBinder.Bind(configuration, definition.SectionPath, options);

        chain.Values.Should().ContainSingle().Which.IsEffective.Should().BeFalse();
        chain.Values[0].Source.PriorityIndex.Should().Be(0);
        contributions.Should().ContainSingle().Which.EffectiveValueCount.Should().Be(lowerEffectiveCount);
        var lower = inventories.Single(inventory => inventory.Source.PriorityIndex == 0);
        lower.SuppliedValueCount.Should().Be(4);
        lower.EffectiveValueCount.Should().Be(lowerEffectiveCount);
        lower.Items.Single(item => item.ConfigurationPath == chain.ConfigurationPath).IsEffective.Should().BeFalse();
        inventories.Single(inventory => inventory.Source.PriorityIndex == 1).Items.Should().BeEmpty();
        if (logicalPath == "WorkerId") options.WorkerId.Should().Be(7);
        else if (higherJson.Contains("null", StringComparison.Ordinal)) options.Child.Should().BeNull();
        else if (logicalPath == "Child.Count") options.Child!.Count.Should().Be(3);
        else options.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSourceEvidence_WhenIndependentChainIsNonempty_ShouldMergeUnspecifiedMembersWithoutAttributingAliasValues()
    {
        var definition = BarrierDefinition();
        var configuration = BuildChainedShapeConfiguration(definition, """{"WorkerId":8}""");
        using var lifetime = configuration as IDisposable;
        var inspector = CreateInspector(configuration, definition);

        var lowerMember = inspector.GetSourceChain(definition, LogicalPath.Parse("Child.Count"));
        var overriddenMember = inspector.GetSourceChain(definition, LogicalPath.Parse("WorkerId"));
        var inventories = await inspector.GetSourceInventoriesAsync(TestContext.Current.CancellationToken);
        var options = new BarrierOptions();
        MonicaConfigurationBinder.Bind(configuration, definition.SectionPath, options);

        lowerMember.Values.Should().ContainSingle().Which.IsEffective.Should().BeTrue();
        overriddenMember.Values.Should().ContainSingle().Which.IsEffective.Should().BeFalse();
        options.WorkerId.Should().Be(8);
        options.Child!.Count.Should().Be(81);
        inspector.GetDefinitionContributions(definition).Should().ContainSingle().Which.EffectiveValueCount.Should().Be(3);
        inventories.Single(inventory => inventory.Source.PriorityIndex == 1).SuppliedValueCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSourceEvidence_WhenRealLeafOutranksChainedBarrier_ShouldKeepTheHigherPhysicalWinner()
    {
        var definition = BarrierDefinition();
        var configuration = BuildChainedShapeConfiguration(definition, "{}", highestJson: """{"WorkerId":42}""");
        using var lifetime = configuration as IDisposable;
        var inspector = CreateInspector(configuration, definition);

        var chain = inspector.GetSourceChain(definition, LogicalPath.Parse("WorkerId"));
        var contributions = inspector.GetDefinitionContributions(definition);
        var inventories = await inspector.GetSourceInventoriesAsync(TestContext.Current.CancellationToken);

        chain.Values.Should().HaveCount(2);
        chain.Values[0].Source.PriorityIndex.Should().Be(2);
        chain.Values[0].DisplayValue.Should().Be("42");
        chain.Values[0].IsEffective.Should().BeTrue();
        chain.Values[1].IsEffective.Should().BeFalse();
        contributions.Single(contribution => contribution.Source.PriorityIndex == 2).EffectiveValueCount.Should().Be(1);
        contributions.Single(contribution => contribution.Source.PriorityIndex == 0).EffectiveValueCount.Should().Be(0);
        inventories.Single(inventory => inventory.Source.PriorityIndex == 2).EffectiveValueCount.Should().Be(1);
    }

    [Fact]
    public void GetRuntimeProjectionRevision_WhenPhysicalSourceIsMaskedByChain_ShouldStillDetectItsChangedBytes()
    {
        var definition = BarrierDefinition();
        var configuration = BuildChainedShapeConfiguration(definition, "{}");
        using var lifetime = configuration as IDisposable;
        var inspector = CreateInspector(configuration, definition);
        var path = LogicalPath.Parse("WorkerId");
        var chain = inspector.GetSourceChain(definition, path);
        var sourceKey = chain.Values.Should().ContainSingle().Which.Source.SourceKey;
        var before = inspector.GetRuntimeProjectionRevision(definition, sourceKey);

        configuration.Providers.First().Set(chain.ConfigurationPath, "92");

        inspector.GetRuntimeProjectionRevision(definition, sourceKey).Should().NotBe(before);
        var changed = inspector.GetSourceChain(definition, path).Values.Should().ContainSingle().Which;
        changed.DisplayValue.Should().Be("92");
        changed.IsEffective.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetSourceEvidence_WhenChainAliasesActiveRootOrCycles_ShouldNotQueryAliasesOrClaimALowerWinner(bool cycle)
    {
        var definition = BarrierDefinition();
        var configuration = new GuardedConfigurationRoot();
        var independent = new GuardedConfigurationRoot();
        var other = new GuardedConfigurationRoot();
        var guardedLeaf = new GuardedConfigurationProvider();
        var lower = ProjectionSource(definition, LOWER_BARRIER_JSON).Build(new ConfigurationBuilder());
        lower.Load();
        configuration.ProviderList.Add(lower);
        if (cycle)
        {
            independent.ProviderList.Add(guardedLeaf);
            independent.ProviderList.Add(Chain(other));
            other.ProviderList.Add(Chain(independent));
        }
        configuration.ProviderList.Add(Chain(cycle ? independent : configuration));
        var inspector = CreateInspector(configuration, definition);

        var chain = inspector.GetSourceChain(definition, LogicalPath.Parse("WorkerId"));
        var contributions = inspector.GetDefinitionContributions(definition);
        var inventories = await inspector.GetSourceInventoriesAsync(TestContext.Current.CancellationToken);
        var revision = () => inspector.GetRuntimeProjectionRevision(definition, "provider:1");

        chain.Values.Should().ContainSingle().Which.IsEffective.Should().BeFalse();
        contributions.Should().ContainSingle().Which.EffectiveValueCount.Should().Be(0);
        inventories.Single(inventory => inventory.Source.PriorityIndex == 1).Items.Should().BeEmpty();
        revision.Should().Throw<InvalidOperationException>();
        configuration.QueryCount.Should().Be(0);
        independent.QueryCount.Should().Be(0);
        other.QueryCount.Should().Be(0);
        guardedLeaf.QueryCount.Should().Be(0);
    }

    [Fact]
    public void GetSourceChain_WhenChainedConfigurationIsAnOpaqueSection_ShouldNotAssumeALowerWinner()
    {
        var definition = BarrierDefinition();
        var independent = new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var independentLifetime = independent as IDisposable;
        var configuration = new ConfigurationBuilder().Add(ProjectionSource(definition, LOWER_BARRIER_JSON))
            .AddConfiguration(independent.GetSection("Opaque")).Build();
        using var lifetime = configuration as IDisposable;

        CreateInspector(configuration, definition).GetSourceChain(definition, LogicalPath.Parse("WorkerId"))
            .Values.Should().ContainSingle().Which.IsEffective.Should().BeFalse();
    }

    private const string LOWER_BARRIER_JSON = """{"WorkerId":91,"Child":{"Count":81},"Caption":"lower","entries":["lower"]}""";

    private static IConfigurationRoot BuildChainedShapeConfiguration(ConfigurationDefinition definition, string higherJson,
        bool nested = false, string? highestJson = null)
    {
        IConfigurationRoot independent = new ConfigurationBuilder().Add(ProjectionSource(definition, higherJson)).Build();
        if (nested) independent = new ConfigurationBuilder().AddConfiguration(independent, shouldDisposeConfiguration: true).Build();
        var builder = new ConfigurationBuilder().Add(ProjectionSource(definition, LOWER_BARRIER_JSON))
            .AddConfiguration(independent, shouldDisposeConfiguration: true);
        if (highestJson is not null) builder.Add(ProjectionSource(definition, highestJson));
        return builder.Build();
    }

    private static IConfigurationSource ProjectionSource(ConfigurationDefinition definition, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new ConfigurationValueProjectionSource(ConfigurationValueProjectionFactory.Create(definition.SectionPath, document.RootElement));
    }

    private static ChainedConfigurationProvider Chain(IConfiguration configuration) => new(new ChainedConfigurationSource
    {
        Configuration = configuration
    });

    private sealed class GuardedConfigurationRoot : IConfigurationRoot
    {
        internal List<IConfigurationProvider> ProviderList { get; } = [];
        internal int QueryCount { get; private set; }
        public IEnumerable<IConfigurationProvider> Providers => ProviderList;
        public string? this[string key] { get => throw UnexpectedQuery(); set => throw UnexpectedQuery(); }
        public IEnumerable<IConfigurationSection> GetChildren() => throw UnexpectedQuery();
        public IConfigurationSection GetSection(string key) => throw UnexpectedQuery();
        public IChangeToken GetReloadToken() => throw UnexpectedQuery();
        public void Reload() => throw UnexpectedQuery();
        private InvalidOperationException UnexpectedQuery()
        {
            QueryCount++;
            return new("A configuration alias was queried instead of its proven independent leaves.");
        }
    }

    private sealed class GuardedConfigurationProvider : ConfigurationProvider
    {
        internal int QueryCount { get; private set; }
        public override bool TryGet(string key, out string? value) => throw UnexpectedQuery();
        public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) => throw UnexpectedQuery();
        private InvalidOperationException UnexpectedQuery()
        {
            QueryCount++;
            return new("A leaf was queried before the entire chained graph was proven acyclic.");
        }
    }

    private static ConfigurationDefinition BarrierDefinition()
        => new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(BarrierOptions));

    private static IConfigurationRoot BuildShapeConfiguration(ConfigurationDefinition definition, string higherJson)
    {
        using var lower = JsonDocument.Parse("""{"WorkerId":91,"Child":{"Count":81},"Caption":"lower","entries":["lower"]}""");
        using var higher = JsonDocument.Parse(higherJson);
        return new ConfigurationBuilder()
            .Add(new ConfigurationValueProjectionSource(ConfigurationValueProjectionFactory.Create(definition.SectionPath, lower.RootElement)))
            .Add(new ConfigurationValueProjectionSource(ConfigurationValueProjectionFactory.Create(definition.SectionPath, higher.RootElement)))
            .Build();
    }

    [Configuration("Barrier")]
    private sealed class BarrierOptions
    {
        public BarrierOptions() { }
        public int WorkerId { get; set; } = 7;
        public BarrierChild? Child { get; set; } = new();
        public string? Caption { get; set; } = "default";
        [ConfigurationKeyName("entries")]
        public List<string> Items { get; set; } = ["default"];
    }

    private sealed class BarrierChild
    {
        public int Count { get; set; } = 3;
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private static ConfigurationSourceInspector CreateInspector(IConfiguration configuration, ConfigurationDefinition? definition = null)
    {
        var runtimeContext = new ConfigurationRuntimeContext();
        runtimeContext.Capture(configuration);
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(definition ?? TestConfigurationFactory.Definition());

        return new ConfigurationSourceInspector(
            runtimeContext,
            registry,
            new ConfigurationPathProjector(),
            Options.Create(new ModuleConfigurationOption
            {
                IncludeUnmanagedSourceInventoryItems = false
            }));
    }
}
