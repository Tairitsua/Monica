using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Monica.Configuration.Annotations;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Xunit;

namespace Test.Monica.Configuration.Services;

public class ConfigurationDefinitionScannerTests
{
    [Fact]
    public void Scan_WhenTheRootConstructorIsPrivate_ShouldRejectTheContractWithoutExecutingIt()
    {
        var act = () => CreateScanner().Scan(typeof(PrivateConstructorOptions));

        var fault = act.Should().Throw<ConfigurationValidationExecutionException>().Which;
        fault.Kind.Should().Be(ConfigurationValidationIssueKind.Contract);
        fault.Stage.Should().Be("options-constructor");
        fault.LogicalPath.Should().Be(LogicalPath.Root);
        fault.InnerException.Should().BeNull();
        fault.ToString().Should().NotContain("synthetic-secret");
    }

    [Fact]
    public void Scan_WhenTypeHasScalarProperties_ShouldBuildObjectRootWithScalarChildren()
    {
        var scanner = CreateScanner();

        var definition = scanner.Scan(typeof(SampleOptions));

        definition.DefinitionKey.Should().Be("test.sample");
        definition.SectionPath.Should().Be("Sample:App");
        definition.DisplayName.Should().Be("Sample Options");
        definition.Root.NodeKind.Should().Be(ConfigurationNodeKind.Object);
        definition.Root.Children.Select(x => x.Name).Should().Contain(["WorkerId", "Endpoint", "ApiKey"]);
        definition.Root.Children.Single(x => x.Name == "WorkerId").ValueKind.Should().Be(ConfigurationValueKind.Integer);
    }

    [Fact]
    public void Scan_WhenPropertyHasConfigurationKeyNameAttribute_ShouldUseConfiguredPropertyName()
    {
        var scanner = CreateScanner();

        var definition = scanner.Scan(typeof(SampleOptions));

        var endpoint = definition.Root.Children.Single(x => x.Name == "Endpoint");
        endpoint.RelativePath.Should().Be(LogicalPath.FromProperties("Endpoint"));
        endpoint.ConfigurationPath.Should().Be("Sample:App:Endpoint");
    }

    [Fact]
    public void Scan_WhenPropertyHasOptionSettingAttribute_ShouldApplyMetadata()
    {
        var scanner = CreateScanner();

        var definition = scanner.Scan(typeof(SampleOptions));

        var apiKey = definition.Root.Children.Single(x => x.Name == "ApiKey");
        apiKey.DisplayName.Should().Be("API key");
        apiKey.Description.Should().Be("Credential used by the sample integration.");
        apiKey.IsSensitive.Should().BeTrue();
        apiKey.ReloadBehavior.Should().Be(ConfigurationReloadBehavior.RequiresRestart);
        apiKey.NodeKey.Should().Be("sample.apiKey");
    }

    [Fact]
    public void Scan_WhenPropertiesUseDataAnnotations_ShouldMapThemToValidationRules()
    {
        var scanner = CreateScanner();

        var definition = scanner.Scan(typeof(SampleOptions));

        var worker = definition.Root.Children.Single(x => x.Name == "WorkerId");
        var endpoint = definition.Root.Children.Single(x => x.Name == "Endpoint");
        var apiKey = definition.Root.Children.Single(x => x.Name == "ApiKey");

        worker.ValidationRules.Should().ContainSingle()
            .Which.Should().Be(new RangeRule(0, 1023));
        endpoint.ValidationRules.OfType<RequiredRule>().Should().ContainSingle();
        endpoint.ValidationRules.OfType<RegexRule>().Should().ContainSingle()
            .Which.Pattern.Should().Be("^https://");
        apiKey.ValidationRules.OfType<MaxLengthRule>().Should().ContainSingle()
            .Which.Max.Should().Be(64);
    }

    [Fact]
    public void Scan_WhenScalarStringPropertyHasEditorHint_ShouldTransportHintToNode()
    {
        var scanner = CreateScanner();

        var definition = scanner.Scan(typeof(EditorHintOptions));

        var route = definition.Root.Children.Single(x => x.Name == nameof(EditorHintOptions.Route));
        route.EditorHint.Should().Be("FlightRoute");
        definition.Root.Children.Single(x => x.Name == nameof(EditorHintOptions.Plain))
            .EditorHint.Should().BeNull();
    }

    [Fact]
    public void Scan_WhenNonScalarPropertyHasEditorHint_ShouldTransportHintToNode()
    {
        var scanner = CreateScanner();

        var definition = scanner.Scan(typeof(NonScalarEditorHintOptions));

        definition.Root.Children.Single(x => x.Name == nameof(NonScalarEditorHintOptions.Routes))
            .EditorHint.Should().Be("Airway");
    }

    [Fact]
    public void Scan_WhenRangeUsesDoubleMaxValue_ShouldTreatUpperBoundAsUnbounded()
    {
        var scanner = CreateScanner();

        var definition = scanner.Scan(typeof(WideDoubleOptions));
        var value = definition.Root.Children.Single(x => x.Name == nameof(WideDoubleOptions.Value));

        value.ValidationRules.Should().ContainSingle()
            .Which.Should().Be(new RangeRule(0, null));
    }

    [Fact]
    public void Scan_WhenRangeUsesDoubleMinValue_ShouldTreatLowerBoundAsUnbounded()
    {
        var scanner = CreateScanner();

        var definition = scanner.Scan(typeof(WideDoubleOptions));
        var value = definition.Root.Children.Single(x => x.Name == nameof(WideDoubleOptions.Lower));

        value.ValidationRules.Should().ContainSingle()
            .Which.Should().Be(new RangeRule(null, 1));
    }

    [Fact]
    public void Scan_WhenListItemPropertyIsMarkedAsKey_ShouldUsePropertyConfigurationNameAsItemKey()
    {
        var scanner = CreateScanner();

        var definition = scanner.Scan(typeof(ListOptions));

        var items = definition.Root.Children.Single(x => x.Name == nameof(ListOptions.Items));
        items.ListTemplate.Should().NotBeNull();
        items.ListTemplate!.ItemKeyPropertyName.Should().Be("id");
        items.ListTemplate.SupportsPerItemMutation.Should().BeTrue();
    }

    [Fact]
    public void Scan_WhenListItemTypeHasMultipleKeyProperties_ShouldThrowInvalidOperationException()
    {
        var scanner = CreateScanner();

        var act = () => scanner.Scan(typeof(MultipleKeyListOptions));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*declares multiple list item key properties*");
    }

    [Fact]
    public void Scan_WhenListItemKeyPropertyIsNotScalar_ShouldThrowInvalidOperationException()
    {
        var scanner = CreateScanner();

        var act = () => scanner.Scan(typeof(ComplexKeyListOptions));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be a public scalar property*");
    }

    [Fact]
    public void Scan_WhenTypeDirectlyReferencesItself_ShouldReportTypeChainAndLogicalPath()
    {
        var scanner = CreateScanner();

        var act = () => scanner.Scan(typeof(DirectRecursiveOptions));

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Recursive configuration schema detected");
        exception.Message.Should().Contain("logical path 'Next'");
        exception.Message.Should().Contain(typeof(DirectRecursiveOptions).FullName!);
    }

    [Fact]
    public void Scan_WhenTypesMutuallyReferenceEachOther_ShouldReportCycle()
    {
        var scanner = CreateScanner();

        var act = () => scanner.Scan(typeof(MutualRecursiveOptions));

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("logical path 'First.Second.First'");
        exception.Message.Should().Contain(typeof(MutualFirst).FullName!);
        exception.Message.Should().Contain(typeof(MutualSecond).FullName!);
    }

    [Fact]
    public void Scan_WhenListItemReferencesActiveAncestor_ShouldReportCycle()
    {
        var scanner = CreateScanner();

        var act = () => scanner.Scan(typeof(ListRecursiveOptions));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*logical path 'Items[#*]'*")
            .WithMessage($"*{typeof(ListRecursiveOptions).FullName}*");
    }

    [Fact]
    public void Scan_WhenDictionaryValueReferencesActiveAncestor_ShouldReportCycle()
    {
        var scanner = CreateScanner();

        var act = () => scanner.Scan(typeof(DictionaryRecursiveOptions));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*logical path 'Entries[$*]'*")
            .WithMessage($"*{typeof(DictionaryRecursiveOptions).FullName}*");
    }

    [Fact]
    public void Scan_WhenNullablePropertyReferencesActiveAncestor_ShouldReportCycle()
    {
        var scanner = CreateScanner();

        var act = () => scanner.Scan(typeof(NullableRecursiveOptions));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*logical path 'Next'*")
            .WithMessage($"*{typeof(NullableRecursiveOptions).FullName}*");
    }

    [Fact]
    public void Scan_WhenTypeIsReusedBySiblingBranches_ShouldBuildBothBranches()
    {
        var scanner = CreateScanner();

        var definition = scanner.Scan(typeof(SiblingReuseOptions));

        definition.Root.Children.Should().HaveCount(2);
        definition.Root.Children.Should().AllSatisfy(
            child => child.Children.Should().ContainSingle(node => node.Name == nameof(ReusableBranch.Value)));
    }

    [Fact]
    public void Scan_WhenDeepestNodeIsAtLogicalDepthLimit_ShouldSucceed()
    {
        var scanner = CreateScanner();

        var definition = scanner.Scan(CreateDepthOptionsType(64));

        GetDeepestSingleChild(definition.Root).RelativePath.Depth.Should().Be(64);
    }

    [Fact]
    public void Scan_WhenDeepestNodeExceedsLogicalDepthLimit_ShouldThrowInvalidOperationException()
    {
        var scanner = CreateScanner();

        var act = () => scanner.Scan(CreateDepthOptionsType(65));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*maximum logical depth of 64*")
            .WithMessage("*CLR type chain:*");
    }

    private static ConfigurationDefinitionScanner CreateScanner()
    {
        return new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher());
    }

    private static Type CreateDepthOptionsType(int deepestLogicalDepth)
    {
        var nodeType = typeof(string);
        for (var depth = 1; depth < deepestLogicalDepth; depth++)
        {
            nodeType = typeof(DepthNode<>).MakeGenericType(nodeType);
        }

        return typeof(DepthOptions<>).MakeGenericType(nodeType);
    }

    private static ConfigurationNodeDefinition GetDeepestSingleChild(ConfigurationNodeDefinition node)
    {
        while (node.Children.Count == 1)
        {
            node = node.Children[0];
        }

        return node;
    }

    [Configuration("Sample:App", DefinitionKey = "test.sample", DisplayName = "Sample Options")]
    private sealed class SampleOptions
    {
        [Range(0, 1023)]
        public int WorkerId { get; set; }

        [Required]
        [RegularExpression("^https://")]
        [ConfigurationKeyName("Endpoint")]
        public string? ServiceUrl { get; set; }

        [MaxLength(64)]
        [OptionSetting("API key",
            Description = "Credential used by the sample integration.",
            IsSensitive = true,
            NodeKey = "sample.apiKey",
            ReloadBehavior = ConfigurationReloadBehavior.RequiresRestart)]
        public string? ApiKey { get; set; }
    }

    [Configuration("Sample:PrivateConstructor")]
    private sealed class PrivateConstructorOptions
    {
        private PrivateConstructorOptions() => throw new InvalidOperationException("synthetic-secret");
        public string Value { get; set; } = "safe";
    }

    [Configuration("Sample:WideDouble")]
    private sealed class WideDoubleOptions
    {
        [Range(0d, double.MaxValue)]
        public double Value { get; set; }

        [Range(double.MinValue, 1d)]
        public double Lower { get; set; }
    }

    [Configuration("Sample:EditorHint", DefinitionKey = "test.editorHint")]
    private sealed class EditorHintOptions
    {
        [OptionSetting("Route", EditorHint = "FlightRoute")]
        public string Route { get; set; } = "";

        [OptionSetting("Plain")]
        public string Plain { get; set; } = "";
    }

    [Configuration("Sample:NonScalarEditorHint", DefinitionKey = "test.nonScalarEditorHint")]
    private sealed class NonScalarEditorHintOptions
    {
        [OptionSetting("Routes", EditorHint = "Airway")]
        public List<string> Routes { get; set; } = [];
    }

    [Configuration("Sample:List", DefinitionKey = "test.list")]
    private sealed class ListOptions
    {
        public List<ListItemOptions> Items { get; set; } = [];
    }

    private sealed class ListItemOptions
    {
        [OptionSetting(IsListItemKey = true)]
        [ConfigurationKeyName("id")]
        public string Name { get; set; } = "";
    }

    [Configuration("Sample:MultipleKeys", DefinitionKey = "test.multipleKeys")]
    private sealed class MultipleKeyListOptions
    {
        public List<MultipleKeyItemOptions> Items { get; set; } = [];
    }

    private sealed class MultipleKeyItemOptions
    {
        [OptionSetting(IsListItemKey = true)]
        public string Name { get; set; } = "";

        [OptionSetting(IsListItemKey = true)]
        public string Code { get; set; } = "";
    }

    [Configuration("Sample:ComplexKey", DefinitionKey = "test.complexKey")]
    private sealed class ComplexKeyListOptions
    {
        public List<ComplexKeyItemOptions> Items { get; set; } = [];
    }

    private sealed class ComplexKeyItemOptions
    {
        [OptionSetting(IsListItemKey = true)]
        public NestedKeyOptions Key { get; set; } = new();
    }

    private sealed class NestedKeyOptions
    {
        public string Value { get; set; } = "";
    }

    [Configuration("Sample:DirectRecursive")]
    private sealed class DirectRecursiveOptions
    {
        public DirectRecursiveOptions Next { get; set; } = null!;
    }

    [Configuration("Sample:MutualRecursive")]
    private sealed class MutualRecursiveOptions
    {
        public MutualFirst First { get; set; } = null!;
    }

    private sealed class MutualFirst
    {
        public MutualSecond Second { get; set; } = null!;
    }

    private sealed class MutualSecond
    {
        public MutualFirst First { get; set; } = null!;
    }

    [Configuration("Sample:ListRecursive")]
    private sealed class ListRecursiveOptions
    {
        public List<ListRecursiveOptions> Items { get; set; } = [];
    }

    [Configuration("Sample:DictionaryRecursive")]
    private sealed class DictionaryRecursiveOptions
    {
        public Dictionary<string, DictionaryRecursiveOptions> Entries { get; set; } = [];
    }

    [Configuration("Sample:NullableRecursive")]
    private sealed class NullableRecursiveOptions
    {
        public NullableRecursiveOptions? Next { get; set; }
    }

    [Configuration("Sample:SiblingReuse")]
    private sealed class SiblingReuseOptions
    {
        public ReusableBranch Left { get; set; } = new();

        public ReusableBranch Right { get; set; } = new();
    }

    private sealed class ReusableBranch
    {
        public string Value { get; set; } = "";
    }

    [Configuration("Sample:Depth")]
    private sealed class DepthOptions<T>
    {
        public T Value { get; set; } = default!;
    }

    private sealed class DepthNode<T>
    {
        public T Value { get; set; } = default!;
    }
}
