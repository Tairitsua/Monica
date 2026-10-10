using AwesomeAssertions;
using Monica.Configuration.Models;
using Xunit;

namespace Test.Monica.Configuration.Models;

public sealed class ConfigurationUnifiedVersionApplyPreviewTests
{
    [Fact]
    public void MissingDefinition_targets_are_skipped_and_do_not_block_the_apply()
    {
        var preview = CreatePreview(
            CreateTarget("Definition.Missing", ConfigurationUnifiedVersionApplyTargetStatus.MissingDefinition),
            CreateTarget("Definition.Ready", ConfigurationUnifiedVersionApplyTargetStatus.Ready),
            CreateTarget("Definition.Unchanged", ConfigurationUnifiedVersionApplyTargetStatus.Unchanged));

        preview.SkippedDefinitionKeys.Should().Equal("Definition.Missing");
        preview.SkippedTargets.Should().ContainSingle().Which.IsSkipped.Should().BeTrue();
        preview.ChangeCount.Should().Be(1);
        preview.BlockedCount.Should().Be(0);
        preview.CanApply.Should().BeTrue();
        preview.ChangedTargets.Select(static target => target.DefinitionKey)
            .Should().Equal("Definition.Ready");
    }

    [Fact]
    public void IncompatibleValue_targets_are_skipped_and_do_not_block_the_apply()
    {
        var preview = CreatePreview(
            CreateTarget("Definition.Incompatible", ConfigurationUnifiedVersionApplyTargetStatus.IncompatibleValue),
            CreateTarget("Definition.Missing", ConfigurationUnifiedVersionApplyTargetStatus.MissingDefinition),
            CreateTarget("Definition.Ready", ConfigurationUnifiedVersionApplyTargetStatus.Ready));

        preview.SkippedDefinitionKeys.Should().Equal("Definition.Incompatible", "Definition.Missing");
        preview.SkippedCount.Should().Be(2);
        preview.BlockedCount.Should().Be(0);
        preview.CanApply.Should().BeTrue();
        preview.ChangedTargets.Select(static target => target.DefinitionKey)
            .Should().Equal("Definition.Ready");
    }

    [Fact]
    public void CompatibleSchemaDrift_targets_do_not_require_acknowledgement()
    {
        var preview = CreatePreview(
            CreateTarget("Definition.Drift", ConfigurationUnifiedVersionApplyTargetStatus.CompatibleSchemaDrift),
            CreateTarget("Definition.Ready", ConfigurationUnifiedVersionApplyTargetStatus.Ready));

        preview.CompatibleSchemaDriftCount.Should().Be(1);
        preview.BlockedCount.Should().Be(0);
        preview.CanApply.Should().BeTrue();
    }

    [Fact]
    public void Preview_with_only_skipped_and_unchanged_targets_cannot_be_applied()
    {
        var preview = CreatePreview(
            CreateTarget("Definition.Missing", ConfigurationUnifiedVersionApplyTargetStatus.MissingDefinition),
            CreateTarget(
                "Definition.Incompatible",
                ConfigurationUnifiedVersionApplyTargetStatus.IncompatibleValue),
            CreateTarget("Definition.Unchanged", ConfigurationUnifiedVersionApplyTargetStatus.Unchanged));

        preview.HasChanges.Should().BeFalse();
        preview.CanApply.Should().BeFalse();
    }

    [Fact]
    public void Skipped_targets_do_not_mask_genuinely_blocked_targets()
    {
        var preview = CreatePreview(
            CreateTarget("Definition.Missing", ConfigurationUnifiedVersionApplyTargetStatus.MissingDefinition),
            CreateTarget(
                "Definition.Incompatible",
                ConfigurationUnifiedVersionApplyTargetStatus.IncompatibleValue),
            CreateTarget("Definition.Blocked", ConfigurationUnifiedVersionApplyTargetStatus.RuntimeOutOfSync));

        preview.SkippedCount.Should().Be(2);
        preview.BlockedCount.Should().Be(1);
        preview.CanApply.Should().BeFalse();
    }

    [Fact]
    public void CanApply_WhenReadyTargetHasNoValidationReport_ShouldBeFalse()
    {
        var preview = CreatePreview(
            CreateTarget("Definition.Ready", ConfigurationUnifiedVersionApplyTargetStatus.Ready) with
            {
                ValidationReport = null
            });

        preview.HasChanges.Should().BeTrue();
        preview.ValidationReports.Should().BeEmpty();
        preview.CanApply.Should().BeFalse();
    }

    [Theory]
    [InlineData(ConfigurationValidationCoverage.SchemaOnly)]
    [InlineData(ConfigurationValidationCoverage.Failed)]
    public void CanApply_WhenReadyTargetCoverageIsIncomplete_ShouldBeFalse(
        ConfigurationValidationCoverage coverage)
    {
        var preview = CreatePreview(
            CreateTarget("Definition.Ready", ConfigurationUnifiedVersionApplyTargetStatus.Ready) with
            {
                ValidationReport = CreateCompleteReport("Definition.Ready") with { Coverage = coverage }
            });

        preview.ValidationReports.Should().ContainSingle().Which.Issues.Should().BeEmpty();
        preview.CanApply.Should().BeFalse();
    }

    [Fact]
    public void CanApply_WhenKnownDefinitionHasObjectValidationIssues_ShouldBlockRatherThanSkip()
    {
        const string definitionKey = "Definition.Invalid";
        var report = CreateCompleteReport(definitionKey) with
        {
            Issues =
            [
                new ConfigurationCandidateValidationIssue
                {
                    DefinitionKey = definitionKey,
                    DefinitionDisplayName = definitionKey,
                    LogicalPath = LogicalPath.Root,
                    LogicalPaths = [LogicalPath.Root],
                    Kind = ConfigurationValidationIssueKind.Code,
                    NodeDisplayName = definitionKey,
                    Problem = "The current object rule rejected the captured aggregate."
                }
            ]
        };
        var preview = CreatePreview(
            CreateTarget("Definition.Missing", ConfigurationUnifiedVersionApplyTargetStatus.MissingDefinition),
            CreateTarget("Definition.Ready", ConfigurationUnifiedVersionApplyTargetStatus.Ready),
            CreateTarget(definitionKey, ConfigurationUnifiedVersionApplyTargetStatus.ValidationRejected) with
            {
                ValidationReport = report
            });

        report.IsSchemaValid.Should().BeTrue();
        report.IsValid.Should().BeFalse();
        preview.SkippedDefinitionKeys.Should().Equal("Definition.Missing");
        preview.BlockedCount.Should().Be(1);
        preview.CanApply.Should().BeFalse();
    }

    [Theory]
    [InlineData(ConfigurationValidationCoverage.Complete)]
    [InlineData(ConfigurationValidationCoverage.SchemaOnly)]
    public void CanApply_WhenAdditionalStoredDocumentReportIsInvalidOrIncomplete_ShouldBeFalse(
        ConfigurationValidationCoverage storedCoverage)
    {
        const string definitionKey = "Definition.Masked";
        var effectiveReport = CreateCompleteReport(definitionKey) with { HasEffectiveChange = false };
        var storedReport = CreateCompleteReport(definitionKey) with
        {
            Target = ConfigurationCandidateValidationTarget.StoredDocument,
            Coverage = storedCoverage,
            Issues = storedCoverage == ConfigurationValidationCoverage.Complete
                ?
                [
                    new ConfigurationCandidateValidationIssue
                    {
                        DefinitionKey = definitionKey,
                        DefinitionDisplayName = definitionKey,
                        LogicalPath = LogicalPath.Root,
                        LogicalPaths = [LogicalPath.Root],
                        Kind = ConfigurationValidationIssueKind.Code,
                        NodeDisplayName = definitionKey,
                        Problem = "The masked stored document violates the current object rule."
                    }
                ]
                : []
        };
        var preview = CreatePreview(
            CreateTarget(definitionKey, ConfigurationUnifiedVersionApplyTargetStatus.Ready) with
            {
                ValidationReport = effectiveReport
            }) with
        {
            ValidationReports = [effectiveReport, storedReport]
        };

        effectiveReport.IsValid.Should().BeTrue();
        storedReport.IsValid.Should().BeFalse();
        preview.HasChanges.Should().BeTrue();
        preview.CanApply.Should().BeFalse();
    }

    private static ConfigurationUnifiedVersionApplyPreview CreatePreview(
        params ConfigurationUnifiedVersionApplyTarget[] targets)
    {
        return new ConfigurationUnifiedVersionApplyPreview
        {
            Version = 1,
            PreviewFingerprint = "sha256:test",
            Targets = targets,
            ValidationReports = targets
                .Where(static target => target.ValidationReport is not null)
                .Select(static target => target.ValidationReport!)
                .ToArray()
        };
    }

    private static ConfigurationUnifiedVersionApplyTarget CreateTarget(
        string definitionKey,
        ConfigurationUnifiedVersionApplyTargetStatus status)
    {
        return new ConfigurationUnifiedVersionApplyTarget
        {
            DefinitionKey = definitionKey,
            DisplayName = definitionKey,
            TargetJson = "{}",
            CapturedSchemaHash = "hash",
            CurrentSchemaHash = "hash",
            CurrentSchemaVersion = 1,
            Status = status,
            ValidationReport = status is ConfigurationUnifiedVersionApplyTargetStatus.MissingDefinition
                or ConfigurationUnifiedVersionApplyTargetStatus.IncompatibleValue
                ? null
                : CreateCompleteReport(definitionKey)
        };
    }

    private static ConfigurationCandidateValidationReport CreateCompleteReport(string definitionKey)
    {
        return new ConfigurationCandidateValidationReport
        {
            DefinitionKey = definitionKey,
            DefinitionDisplayName = definitionKey,
            ScopePath = LogicalPath.Root,
            Scope = ConfigurationValidationScope.CompleteAggregate,
            Coverage = ConfigurationValidationCoverage.Complete,
            ValidationRevision = "revision:test"
        };
    }
}
