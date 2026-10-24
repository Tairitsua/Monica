using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Monica.Core.Configuration;
using Xunit;

namespace Test.Monica.Core.Configuration;

public sealed class ConfigurationChildKeyIndexTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(":")]
    [InlineData("settings")]
    [InlineData("Settings:Items")]
    [InlineData("SETTINGS:ITEMS:2")]
    [InlineData("Settings:Empty")]
    [InlineData("missing")]
    public void GetChildKeys_WhenPathsAndProvidersOverlap_ShouldMatchMicrosoftProvider(string? parentPath)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = null, [":"] = "empty", ["::Nested"] = "nested", [":Leading"] = "leading",
            ["Settings:Items:10:Name"] = "ten", ["Settings:Items:2:Name"] = "two",
            ["Settings:Items:2:Enabled"] = "true", ["Settings:Empty"] = null,
            ["Settings:Trailing:"] = "trailing", ["SettingsOther:Value"] = "other"
        };
        var expected = new ReferenceProvider(data);
        var index = new ConfigurationChildKeyIndex();
        index.GetChildKeys(data, [], parentPath).Should().Equal(expected.GetChildKeys([], parentPath));

        // Lazy earlier contributions retain duplicates and participate in numeric ordering.
        var earlier = new[] { "20", "2", "10", "2", "Extra" }.Where(_ => true);
        index.GetChildKeys(data, earlier, parentPath).Should().Equal(expected.GetChildKeys(earlier, parentPath));
    }

    [Fact]
    public void GetChildKeys_WhenDataIsReplacedWithoutReloadNotification_ShouldUseNewKeysAndPreservePriorEnumeration()
    {
        var index = new ConfigurationChildKeyIndex();
        var first = new Dictionary<string, string?> { ["Settings:Old"] = "one" };
        var second = new Dictionary<string, string?> { ["Settings:New"] = "two" };
        var oldKeys = index.GetChildKeys(first, [], "Settings");

        index.GetChildKeys(second, [], "Settings").Should().Equal("New");
        oldKeys.Should().Equal("Old");
        index.GetChildKeys(second, ["External"], "Settings").Should().Equal("External", "New");
        index.GetChildKeys(second, [], "Settings").Should().Equal("New");
    }

    private sealed class ReferenceProvider : ConfigurationProvider
    {
        internal ReferenceProvider(IDictionary<string, string?> data) => Data = data;
    }
}
