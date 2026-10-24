using System.Text;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Formats source-aware runtime validation diagnostics for logging and opt-in enforcement.
/// </summary>
internal static class ConfigurationRuntimeValidationMessageFormatter
{
    /// <summary>
    /// Formats a full report as a diagnostic warning that explains why the application continues.
    /// </summary>
    /// <param name="report">The validation report.</param>
    /// <returns>The formatted message.</returns>
    public static string FormatDiagnosticReport(ConfigurationValidationReport report)
    {
        return FormatReport(
            report,
            "Monica runtime configuration validation found",
            $"Monica will not reject application startup or managed options resolution for these findings because {nameof(ConfigurationRuntimeValidationBehavior.DiagnosticOnly)} is configured.");
    }

    /// <summary>
    /// Formats a full report as a fail-fast enforcement exception message.
    /// </summary>
    /// <param name="report">The validation report.</param>
    /// <returns>The formatted message.</returns>
    public static string FormatFailFastReport(ConfigurationValidationReport report)
    {
        return FormatReport(
            report,
            "Monica runtime configuration validation failed:",
            $"{nameof(ConfigurationRuntimeValidationBehavior.FailFast)} is configured.");
    }

    internal static string FormatStartupReport(ConfigurationValidationReport report)
        => FormatReport(report, "Monica Startup configuration validation failed:",
            "Startup inputs must be valid before composition completes.", startup: true);

    private static string FormatReport(
        ConfigurationValidationReport report,
        string summaryPrefix,
        string behaviorExplanation,
        bool startup = false)
    {
        var affectedDefinitionCount = report.Issues
            .Select(issue => issue.DefinitionKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var builder = new StringBuilder();
        builder.Append(summaryPrefix)
            .Append(' ')
            .Append(report.IssueCount)
            .Append(report.IssueCount == 1 ? " issue across " : " issues across ")
            .Append(affectedDefinitionCount)
            .Append(startup
                ? affectedDefinitionCount == 1 ? " Startup definition." : " Startup definitions."
                : affectedDefinitionCount == 1 ? " managed definition." : " managed definitions.");

        builder.Append(' ').Append(behaviorExplanation);

        foreach (var group in report.Issues
                     .GroupBy(issue => issue.DefinitionKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var issue in group.OrderBy(issue => issue.LogicalPath.ToCanonicalString(), StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine()
                    .AppendLine(FormatIssueHeader(issue))
                    .AppendLine($"Path: {DisplayLogicalPath(issue.LogicalPath)}")
                    .AppendLine($"Configuration key: {issue.ConfigurationPath}")
                    .AppendLine($"Problem: {issue.Problem}")
                    .AppendLine($"Effective value: {DisplayEffectiveValue(issue)}")
                    .AppendLine($"Effective source: {DisplayEffectiveSource(issue)}")
                    .Append($"Source chain: {DisplaySourceChain(issue)}");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats one issue for Microsoft.Extensions.Options validation.
    /// </summary>
    /// <param name="issue">The issue to format.</param>
    /// <returns>The formatted issue.</returns>
    public static string FormatOptionsIssue(ConfigurationRuntimeValidationIssue issue)
    {
        return $"{FormatIssueHeader(issue)} {DisplayLogicalPath(issue.LogicalPath)} ({issue.ConfigurationPath}): {issue.Problem} " +
               $"Effective value: {DisplayEffectiveValue(issue)}; Effective source: {DisplayEffectiveSource(issue)}; " +
               $"Source chain: {DisplaySourceChain(issue)}";
    }

    private static string FormatIssueHeader(ConfigurationRuntimeValidationIssue issue)
    {
        var displayParts = new[] { issue.DefinitionDisplayName, issue.DefinitionCategory, issue.NodeDisplayName }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var display = displayParts.Length == 0 ? null : string.Join(" / ", displayParts);
        return string.IsNullOrWhiteSpace(display)
            ? $"[{DisplayDefinitionKey(issue.DefinitionKey)}]"
            : $"[{DisplayDefinitionKey(issue.DefinitionKey)}] {display}";
    }

    private static string DisplayDefinitionKey(string definitionKey)
    {
        var lastSeparator = Math.Max(definitionKey.LastIndexOf('.'), definitionKey.LastIndexOf('+'));
        return lastSeparator >= 0 ? definitionKey[(lastSeparator + 1)..] : definitionKey;
    }

    private static string DisplayLogicalPath(LogicalPath logicalPath)
    {
        return logicalPath.Depth == 0
            ? "$"
            : $"$.{logicalPath.ToCanonicalString()}";
    }

    private static string DisplayEffectiveValue(ConfigurationRuntimeValidationIssue issue)
    {
        if (issue.IsMissing)
        {
            return "<missing>";
        }

        if (issue.IsSensitive)
        {
            return "<redacted>";
        }

        return string.IsNullOrWhiteSpace(issue.EffectiveDisplayValue)
            ? "<empty>"
            : issue.EffectiveDisplayValue;
    }

    private static string DisplayEffectiveSource(ConfigurationRuntimeValidationIssue issue)
    {
        return issue.EffectiveSource is null
            ? "none"
            : $"{issue.EffectiveSource.DisplayName} ({issue.EffectiveSource.ProviderType})";
    }

    private static string DisplaySourceChain(ConfigurationRuntimeValidationIssue issue)
    {
        if (issue.SourceChain.Values.Count == 0)
        {
            return "no provider supplies this key.";
        }

        return string.Join(
            " -> ",
            issue.SourceChain.Values.Select(value =>
                value.IsEffective
                    ? $"{value.Source.DisplayName} (effective)"
                    : value.Source.DisplayName));
    }
}
