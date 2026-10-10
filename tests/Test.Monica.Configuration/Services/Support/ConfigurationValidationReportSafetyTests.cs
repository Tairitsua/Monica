using System.Text.Json;
using AwesomeAssertions;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Annotations;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services.Support;

public sealed class ConfigurationValidationReportSafetyTests
{
    [Theory]
    [InlineData("{\"Items\":{\"Token\":\"synthetic-secret\"},\"Label\":\"safe\"}")]
    [InlineData("{\"Items\":[],\"Label\":{\"Token\":\"synthetic-secret\"}}")]
    [InlineData("{\"Items\":[],\"Label\":\"safe\",\"retired\":{\"Token\":\"synthetic-secret\"}}")]
    public void Reports_WhenRejectedShapesContainSensitiveDescendants_ShouldExposePathsWithoutJsonOrSourceValues(string json)
    {
        var definition = new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(SensitiveContainerOptions));
        var result = ConfigurationValidationTestServices.CreateCoordinator(typeof(SensitiveContainerOptions))
            .ValidateCompleteValue(definition, json);
        var inspector = Substitute.For<IConfigurationSourceInspector>();
        var runtime = ConfigurationValidationReportFactory.Create(definition, result, inspector);
        var candidate = result.Issues.Select(issue => ConfigurationCandidateValidationService.ToCandidateIssue(definition, issue)).ToArray();

        result.IsValid.Should().BeFalse();
        runtime.Issues.Should().OnlyContain(issue => issue.EffectiveDisplayValue == null && issue.SourceChain.Values.Count == 0);
        candidate.Should().OnlyContain(issue => issue.CandidateDisplayValue == null);
        JsonSerializer.Serialize(runtime).Should().NotContain("synthetic-secret");
        JsonSerializer.Serialize(candidate).Should().NotContain("synthetic-secret");
        inspector.DidNotReceive().GetSourceChain(Arg.Any<ConfigurationDefinition>(), Arg.Any<LogicalPath>());
    }

    [Configuration("ReportSafety")]
    private sealed class SensitiveContainerOptions
    {
        public List<SensitiveItem> Items { get; set; } = [];
        public string Label { get; set; } = "safe";
    }

    private sealed class SensitiveItem
    {
        [OptionSetting(IsSensitive = true)] public string Token { get; set; } = string.Empty;
    }
}
