using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AwesomeAssertions;
using Monica.Configuration.Annotations;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Xunit;

namespace Test.Monica.Configuration.Services.Support;

public sealed class ConfigurationOptionsValidationDiagnosticsTests
{
    [Fact]
    public void ObservedAttempt_WhenCallersTryToMutateNestedCollections_ShouldRetainAnImmutableSafeSnapshot()
    {
        var definition = new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(DiagnosticOptions));
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(definition);
        var diagnostics = new ConfigurationOptionsValidationDiagnostics(registry);
        var result = ConfigurationValidationTestServices.CreateCoordinator(typeof(DiagnosticOptions))
            .ValidateInstance(definition, new DiagnosticOptions { Mode = (DiagnosticMode)99 });
        var recorded = diagnostics.Record(definition, result);
        var original = JsonSerializer.Serialize(recorded);

        var replaceReport = () => ((IList<ConfigurationOptionsValidationReport>)diagnostics.GetReports())[0]
            = recorded with { DefinitionKey = "changed" };
        replaceReport.Should().Throw<NotSupportedException>();
        var replaceIssue = () => ((IList<ConfigurationRuntimeValidationIssue>)recorded.Issues)[0]
            = recorded.Issues[0] with { Problem = "changed" };
        replaceIssue.Should().Throw<NotSupportedException>();
        foreach (var issue in recorded.Issues)
        {
            var replacePath = () => ((IList<LogicalPath>)issue.LogicalPaths)[0] = LogicalPath.Root;
            replacePath.Should().Throw<NotSupportedException>();
            var replaceSegment = () => ((IList<ConfigurationPathSegment>)issue.LogicalPath.Segments)[0]
                = new PropertySegment("changed");
            replaceSegment.Should().Throw<NotSupportedException>();
            foreach (var location in issue.LogicalPaths)
            {
                var replaceLocation = () => ((IList<ConfigurationPathSegment>)location.Segments)[0]
                    = new PropertySegment("changed");
                replaceLocation.Should().Throw<NotSupportedException>();
            }
        }
        var schemaIssue = recorded.Issues.Single(issue => issue.Kind == ConfigurationValidationIssueKind.Schema);
        var replaceRule = () => ((IList<ConfigurationValidationRule>)schemaIssue.ValidationRules)[0] = new RequiredRule();
        replaceRule.Should().Throw<NotSupportedException>();
        var allowed = schemaIssue.ValidationRules.OfType<AllowedValuesRule>().Single();
        var replaceAllowedValue = () => ((IList<string>)allowed.Values)[0] = "changed";
        replaceAllowedValue.Should().Throw<NotSupportedException>();

        // Definition metadata is a separate owner; subsequent mutations cannot rewrite an observed attempt.
        var sourceAllowed = result.Issues.Single(issue => issue.Kind == ConfigurationValidationIssueKind.Schema)
            .ValidationRules.OfType<AllowedValuesRule>().Single();
        ((IList<string>)sourceAllowed.Values)[0] = "changed";

        diagnostics.GetLatestReport(definition.DefinitionKey).Should().BeSameAs(recorded);
        diagnostics.GetReports().Should().ContainSingle().Which.Should().BeSameAs(recorded);
        JsonSerializer.Serialize(recorded).Should().Be(original).And.NotContain("synthetic-secret");
        recorded.Issues.Should().OnlyContain(issue => issue.EffectiveDisplayValue == null && issue.SourceChain.Values.Count == 0);
    }

    [Configuration("DiagnosticImmutability")]
    private sealed class DiagnosticOptions : IValidatableObject
    {
        public DiagnosticMode Mode { get; set; } = DiagnosticMode.First;
        public DiagnosticMode OtherMode { get; set; } = DiagnosticMode.First;
        [OptionSetting(IsSensitive = true)] public string PrivatePayload { get; set; } = "synthetic-secret";

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Mode != OtherMode)
                yield return new ValidationResult("The modes must agree.", [nameof(Mode), nameof(OtherMode)]);
        }
    }

    private enum DiagnosticMode { First, Second }
}
