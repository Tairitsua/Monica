using System.Collections;
using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Monica.Configuration.Annotations;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Xunit;

namespace Test.Monica.Configuration.Services.Support;

public sealed class ConfigurationObjectValidationWalkerTests
{
    [Fact]
    public void ValidateCompleteValue_WhenRangeAndAggregateRulesFail_ShouldReportBothWithAllAliases()
    {
        var definition = Scan<RangeOptions>();
        var result = ConfigurationValidationTestServices.CreateCoordinator(typeof(RangeOptions))
            .ValidateCompleteValue(definition, """{"lower":50,"Maximum":20}""");

        result.Coverage.Should().Be(ConfigurationValidationCoverage.Complete);
        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Kind == ConfigurationValidationIssueKind.Schema);
        var code = result.Issues.Where(issue => issue.Kind == ConfigurationValidationIssueKind.Code).ToArray();
        code.Select(issue => issue.LogicalPath.ToString()).Should().Equal("lower", "Maximum");
        code.Should().OnlyContain(issue => issue.LogicalPaths.Select(path => path.ToString()).SequenceEqual(new[] { "lower", "Maximum" }));
    }

    [Fact]
    public void ValidateInstance_WhenNestedListDictionaryAndSharedObjectsFail_ShouldKeepEachOwnedPath()
    {
        var definition = Scan<GraphOptions>();
        var shared = new ChildValues { Key = "shared", Invalid = true };
        var options = new GraphOptions
        {
            Left = shared, Right = shared,
            Items = [new ChildValues { Key = "item-a", Invalid = true }],
            Map = new Dictionary<string, ChildValues> { ["entry"] = new() { Invalid = true } }
        };
        var result = ConfigurationValidationTestServices.CreateCoordinator(typeof(GraphOptions)).ValidateInstance(definition, options);

        result.Scope.Should().Be(ConfigurationValidationScope.ActualOptions);
        result.Issues.Where(issue => issue.Kind == ConfigurationValidationIssueKind.Code)
            .Select(issue => issue.LogicalPath.ToString()).Should().BeEquivalentTo(
                "Left.Invalid", "Right.Invalid", "Items[#item-a].Invalid", "Map[$entry].Invalid");
    }

    [Fact]
    public void ValidateInstance_WhenSensitiveSiblingExists_ShouldRedactMemberlessAndMultiMemberMessages()
    {
        var result = ConfigurationValidationTestServices.CreateCoordinator(typeof(SensitiveOptions))
            .ValidateInstance(Scan<SensitiveOptions>(), new SensitiveOptions());

        result.Issues.Should().HaveCount(3);
        result.Issues.Should().OnlyContain(issue => issue.IsSensitive && issue.DisplayValue == null);
        result.Issues.Should().OnlyContain(issue => !issue.Message.Contains("synthetic-secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, ConfigurationValidationIssueKind.Contract, "validation-member")]
    [InlineData(true, ConfigurationValidationIssueKind.Execution, "object-rule")]
    public void ValidateInstance_WhenCodeContractOrIteratorFails_ShouldExposeOnlySafeFault(bool iterator,
        ConfigurationValidationIssueKind kind, string stage)
    {
        var coordinator = ConfigurationValidationTestServices.CreateCoordinator(typeof(FaultOptions));
        var act = () => coordinator.ValidateInstance(Scan<FaultOptions>(), new FaultOptions { IteratorFault = iterator });

        var fault = act.Should().Throw<ConfigurationValidationExecutionException>().Which;
        fault.Kind.Should().Be(kind);
        fault.Stage.Should().Be(stage);
        fault.InnerException.Should().BeNull();
        fault.ToString().Should().NotContain("synthetic-secret");
    }

    [Fact]
    public void Scan_WhenUnsupportedAttributeIsPresent_ShouldRejectTheContractInsteadOfReportingComplete()
    {
        var act = () => Scan<UnsupportedOptions>();
        act.Should().Throw<ConfigurationValidationExecutionException>().Which.Kind.Should().Be(ConfigurationValidationIssueKind.Contract);
    }

    [Fact]
    public void ValidateCompleteValue_WhenMetadataLacksLocalAuthority_ShouldKeepCoverageIncomplete()
    {
        var definition = Scan<RangeOptions>() with { Origin = ConfigurationDefinitionOrigin.PublishedMetadata };
        var result = ConfigurationValidationTestServices.CreateCoordinator().ValidateCompleteValue(definition,
            """{"lower":10,"Maximum":20}""");
        result.IsSchemaValid.Should().BeTrue();
        result.Coverage.Should().Be(ConfigurationValidationCoverage.SchemaOnly);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateCompleteValue_WhenPublishedMetadataMatchesALocalKeyButNotItsAuthority_ShouldRejectCodeExecution()
    {
        var coordinator = ConfigurationValidationTestServices.CreateCoordinator(typeof(RangeOptions));
        var remote = Scan<RangeOptions>() with { Origin = ConfigurationDefinitionOrigin.PublishedMetadata };
        var act = () => coordinator.ValidateCompleteValue(remote, """{"lower":10,"Maximum":20}""");
        act.Should().Throw<ConfigurationValidationExecutionException>().Which.Kind.Should().Be(ConfigurationValidationIssueKind.Contract);
        var changed = Scan<RangeOptions>() with { ValidationContract = new()
        {
            Capability = ConfigurationValidationCapability.ObjectCode, Revision = "sha256:changed"
        } };
        var changedAct = () => coordinator.ValidateCompleteValue(changed, """{"lower":10,"Maximum":20}""");
        changedAct.Should().Throw<ConfigurationValidationExecutionException>().Which.Stage.Should().Be("local-validation-contract");
    }

    [Fact]
    public void ValidateCompleteValue_WhenRequiredRawFieldsAreMissing_ShouldNotHideThemBehindValidDefaults()
    {
        var result = ConfigurationValidationTestServices.CreateCoordinator(typeof(RangeOptions))
            .ValidateCompleteValue(Scan<RangeOptions>(), "{}");
        result.IsValid.Should().BeFalse();
        result.Issues.Where(issue => issue.IsMissing).Select(issue => issue.LogicalPath.ToString())
            .Should().BeEquivalentTo("lower", "Maximum");
    }

    [Fact]
    public void ValidateCompleteValue_WhenHistoricalFieldsWereRemoved_ShouldTolerateOnlyTheCapturedProfile()
    {
        var coordinator = ConfigurationValidationTestServices.CreateCoordinator(typeof(RangeOptions));
        var definition = Scan<RangeOptions>();
        const string JSON = """{"lower":10,"Maximum":20,"retired":"historical"}""";
        coordinator.ValidateCompleteValue(definition, JSON, ConfigurationValidationProfile.CapturedValue).IsValid.Should().BeTrue();
        coordinator.ValidateCompleteValue(definition, JSON).IsValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateSourceContribution_WhenPartialOrShadowableRuleFailuresExist_ShouldOnlyRejectProjectionAmbiguities()
    {
        var coordinator = ConfigurationValidationTestServices.CreateCoordinator(typeof(RangeOptions));
        var definition = Scan<RangeOptions>();
        coordinator.ValidateSourceContribution(definition, """{"lower":50}""").Should().BeEmpty();
        coordinator.ValidateSourceContribution(definition, """{"lower":10,"LOWER":20}""")
            .Should().ContainSingle().Which.BlocksProjection.Should().BeTrue();
        coordinator.ValidateSourceContribution(definition, """{"foreign":"value"}""")
            .Should().ContainSingle().Which.BlocksProjection.Should().BeTrue();
    }

    [Fact]
    public void ValidateInstance_WhenValidatorReturnsANullSequence_ShouldTreatItAsNoObjectFailures()
    {
        var result = ConfigurationValidationTestServices.CreateCoordinator(typeof(NullSequenceOptions))
            .ValidateInstance(Scan<NullSequenceOptions>(), new NullSequenceOptions());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateInstance_WhenAGetterThrows_ShouldExposeAPropertyScopedSafeExecutionFault()
    {
        var act = () => ConfigurationValidationTestServices.CreateCoordinator(typeof(GetterFaultOptions))
            .ValidateInstance(Scan<GetterFaultOptions>(), new GetterFaultOptions());
        var fault = act.Should().Throw<ConfigurationValidationExecutionException>().Which;
        fault.Stage.Should().Be("property-read");
        fault.LogicalPath.ToString().Should().Be("Value");
        fault.ToString().Should().NotContain("synthetic-secret");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Validate_WhenACollectionIteratorForgesAFrameworkFault_ShouldSanitizeBothSnapshotAndWalker(
        bool dictionary, bool lazy, bool walkerOnly)
    {
        var definition = Scan<CollectionFaultOptions>();
        var options = new CollectionFaultOptions();
        if (dictionary) options.Map = new FaultingDictionary(lazy);
        else options.Items = new FaultingList(lazy);
        var registry = ConfigurationValidationTestServices.CreateLocalRegistry(typeof(CollectionFaultOptions));
        Action act = walkerOnly
            ? () => new ConfigurationObjectValidationWalker().Validate(registry.GetRequired(definition), options)
            : () => new ConfigurationObjectMaterializer(registry).Snapshot(definition, options);

        var fault = act.Should().Throw<ConfigurationValidationExecutionException>().Which;
        fault.Kind.Should().Be(ConfigurationValidationIssueKind.Execution);
        fault.Stage.Should().Be(walkerOnly ? "object-traversal" : "object-snapshot");
        fault.DefinitionKey.Should().Be(definition.DefinitionKey);
        fault.LogicalPath.ToString().Should().Be(dictionary ? "Map" : "Items");
        fault.InnerException.Should().BeNull();
        fault.ToString().Should().NotContain("synthetic-secret");
    }

    [Fact]
    public void ValidateInstance_WhenAGetterForgesAFrameworkFault_ShouldKeepThePropertyScopedExecutionBoundary()
    {
        var definition = Scan<ForgedGetterFaultOptions>();
        var act = () => ConfigurationValidationTestServices.CreateCoordinator(typeof(ForgedGetterFaultOptions))
            .ValidateInstance(definition, new ForgedGetterFaultOptions());

        var fault = act.Should().Throw<ConfigurationValidationExecutionException>().Which;
        fault.Kind.Should().Be(ConfigurationValidationIssueKind.Execution);
        fault.Stage.Should().Be("property-read");
        fault.DefinitionKey.Should().Be(definition.DefinitionKey);
        fault.LogicalPath.ToString().Should().Be("Value");
        fault.InnerException.Should().BeNull();
        fault.ToString().Should().NotContain("synthetic-secret");
    }

    [Fact]
    public void ValidateCompleteValue_WhenConstructorThrows_ShouldExposeOnlyASafeExecutionFault()
    {
        var act = () => ConfigurationValidationTestServices.CreateCoordinator(typeof(ConstructorFaultOptions))
            .ValidateCompleteValue(Scan<ConstructorFaultOptions>(), """{"Value":1}""");
        var fault = act.Should().Throw<ConfigurationValidationExecutionException>().Which;
        fault.Stage.Should().Be("object-materialization");
        fault.ToString().Should().NotContain("synthetic-secret");
    }

    [Fact]
    public void ValidateInstance_WhenAnUndeclaredSubtypeAppears_ShouldRefuseCompleteCoverage()
    {
        var act = () => ConfigurationValidationTestServices.CreateCoordinator(typeof(PolymorphicOptions))
            .ValidateInstance(Scan<PolymorphicOptions>(), new PolymorphicOptions { Child = new DerivedValues() });
        act.Should().Throw<ConfigurationValidationExecutionException>().Which.Kind.Should().Be(ConfigurationValidationIssueKind.Contract);
    }

    private static ConfigurationDefinition Scan<T>() => new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(T));

    [Fact]
    public void ValidateCompleteValue_WhenDifferentRulesShareAMessageAndMember_ShouldPreserveAllRelatedLocations()
    {
        var result = ConfigurationValidationTestServices.CreateCoordinator(typeof(OverlappingRulesOptions))
            .ValidateCompleteValue(Scan<OverlappingRulesOptions>(), """{"A":1,"B":2,"C":3}""");
        var failures = result.Issues.Where(issue => issue.Kind == ConfigurationValidationIssueKind.Code).ToArray();
        failures.Should().HaveCount(4);
        failures.Where(issue => issue.LogicalPath.ToString() == "A").Select(issue =>
            string.Join(',', issue.LogicalPaths.Select(path => path.ToString()))).Should().Equal("A,B", "A,C");
    }

    [Fact]
    public void Scan_WhenTwoPropertiesClaimTheSameConfigurationAlias_ShouldExposeASafeContractFault()
    {
        var act = () => Scan<DuplicateAliasOptions>();
        act.Should().Throw<ConfigurationValidationExecutionException>().Which.Stage.Should().Be("duplicate-configuration-alias");
    }

    [Fact]
    public void ValidateCompleteValue_WhenTheRootIsExplicitNull_ShouldNotSubstituteConstructorDefaults()
    {
        var result = ConfigurationValidationTestServices.CreateCoordinator(typeof(RangeOptions))
            .ValidateCompleteValue(Scan<RangeOptions>(), "null");
        result.Coverage.Should().Be(ConfigurationValidationCoverage.Failed);
        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.LogicalPath.Depth == 0 && issue.BlocksMaterialization);
    }

    [Configuration("ObjectRange")]
    private sealed class RangeOptions : IValidatableObject
    {
        [ConfigurationKeyName("lower"), Range(0, 30)] public int Minimum { get; set; } = 10;
        public int Maximum { get; set; } = 20;
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            yield return ValidationResult.Success!;
            if (Minimum > Maximum) yield return new ValidationResult("Minimum must not exceed Maximum.", [nameof(Minimum), nameof(Maximum)]);
        }
    }

    [Configuration("ObjectGraph")]
    private sealed class GraphOptions
    {
        public ChildValues? Left { get; set; }
        public ChildValues? Right { get; set; }
        public List<ChildValues> Items { get; set; } = [];
        public Dictionary<string, ChildValues> Map { get; set; } = [];
    }

    private sealed class ChildValues : IValidatableObject
    {
        [OptionSetting(IsListItemKey = true)] public string Key { get; set; } = "default";
        public bool Invalid { get; set; }
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Invalid) yield return new ValidationResult("Child failed.", [nameof(Invalid)]);
        }
    }

    [Configuration("ObjectSensitive")]
    private sealed class SensitiveOptions : IValidatableObject
    {
        [OptionSetting(IsSensitive = true)] public string Token { get; set; } = "synthetic-secret";
        public int Other { get; set; }
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            yield return new ValidationResult(Token);
            yield return new ValidationResult(Token, [nameof(Token), nameof(Other)]);
        }
    }

    [Configuration("ObjectFault")]
    private sealed class FaultOptions : IValidatableObject
    {
        public bool IteratorFault { get; set; }
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (IteratorFault) throw new InvalidOperationException("synthetic-secret");
            yield return new ValidationResult("synthetic-secret", ["UnknownProperty"]);
        }
    }

    [Configuration("ObjectUnsupported")]
    private sealed class UnsupportedOptions
    {
        [CustomRule] public string Value { get; set; } = string.Empty;
    }
    private sealed class CustomRuleAttribute : ValidationAttribute;

    [Configuration("ObjectNullSequence")]
    private sealed class NullSequenceOptions : IValidatableObject
    {
        public int Value { get; set; }
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) => null!;
    }
    [Configuration("ObjectGetterFault")]
    private sealed class GetterFaultOptions { public int Value => throw new InvalidOperationException("synthetic-secret"); }
    [Configuration("ObjectForgedGetterFault")]
    private sealed class ForgedGetterFaultOptions { public int Value => throw ForgedFault(); }
    [Configuration("ObjectCollectionFault")]
    private sealed class CollectionFaultOptions
    {
        public List<int> Items { get; set; } = [];
        public Dictionary<string, int> Map { get; set; } = [];
    }
    private sealed class FaultingList(bool lazy) : List<int>, IEnumerable
    {
        IEnumerator IEnumerable.GetEnumerator() => lazy ? new FaultingEnumerator() : throw ForgedFault();
    }
    private sealed class FaultingDictionary(bool lazy) : Dictionary<string, int>, IEnumerable
    {
        IEnumerator IEnumerable.GetEnumerator() => lazy ? new FaultingEnumerator() : throw ForgedFault();
    }
    private sealed class FaultingEnumerator : IEnumerator
    {
        public object Current => throw ForgedFault();
        public bool MoveNext() => throw ForgedFault();
        public void Reset() => throw ForgedFault();
    }
    private static ConfigurationValidationExecutionException ForgedFault() => new("synthetic-secret-owner",
        LogicalPath.Root.Append(new PropertySegment("synthetic-secret-path")), "synthetic-secret",
        ConfigurationValidationIssueKind.Contract);
    [Configuration("ObjectConstructorFault")]
    private sealed class ConstructorFaultOptions
    {
        public ConstructorFaultOptions() => throw new InvalidOperationException("synthetic-secret");
        public int Value { get; set; }
    }
    [Configuration("ObjectPolymorphic")]
    private sealed class PolymorphicOptions { public BaseValues Child { get; set; } = new(); }
    private class BaseValues { public int Value { get; set; } }
    private sealed class DerivedValues : BaseValues { public string Undeclared { get; set; } = string.Empty; }
    [Configuration("ObjectOverlapping")]
    private sealed class OverlappingRulesOptions : IValidatableObject
    {
        public int A { get; set; }
        public int B { get; set; }
        public int C { get; set; }
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            yield return new ValidationResult("Shared constraint.", [nameof(A), nameof(B)]);
            yield return new ValidationResult("Shared constraint.", [nameof(A), nameof(C)]);
        }
    }
    [Configuration("ObjectDuplicateAlias")]
    private sealed class DuplicateAliasOptions
    {
        [ConfigurationKeyName("shared")] public int A { get; set; }
        [ConfigurationKeyName("SHARED")] public int B { get; set; }
    }
}
