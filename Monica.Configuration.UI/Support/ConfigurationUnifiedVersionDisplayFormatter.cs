using Monica.Configuration.Models;

namespace Monica.Configuration.UI.Support;

internal static class ConfigurationUnifiedVersionDisplayFormatter
{
    public static string VersionLabel(ConfigurationUnifiedVersionSummary version)
    {
        return ConfigurationUnifiedVersionJsonFormatter.VersionLabel(version.Version);
    }

    public static string DefinitionLabel(string definitionKey)
    {
        var separator = definitionKey.LastIndexOf('.');
        return separator < 0 || separator == definitionKey.Length - 1
            ? definitionKey
            : definitionKey[(separator + 1)..];
    }

    public static string OperatorText(ConfigurationUnifiedVersionSummary version, string emptyText)
    {
        return OperatorTextOrNull(version) ?? emptyText;
    }

    public static string? OperatorTextOrNull(ConfigurationUnifiedVersionSummary version)
    {
        return !string.IsNullOrWhiteSpace(version.ModifierName)
            ? version.ModifierName!
            : !string.IsNullOrWhiteSpace(version.ModifierId)
                ? version.ModifierId!
                : null;
    }

    public static string ValueOrEmpty(string? value, string emptyText)
    {
        return string.IsNullOrWhiteSpace(value) ? emptyText : value;
    }
}
