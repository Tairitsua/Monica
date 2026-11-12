using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core.Modularity.Extensions;
using Monica.Framework.UI.Localization;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Framework.UI.Seeder;

public sealed class SeederResourceExtensionsTests
{
    [Theory]
    [InlineData(
        "en-US", 1,
        "1 of 1 seeder reached a terminal state", "Showing 1 of 1 seeder", "1 dependency",
        "1 recorded attempt · up to 1 per run", "1 attempt recorded")]
    [InlineData(
        "en-US", 3,
        "1 of 3 seeders reached a terminal state", "Showing 1 of 3 seeders", "3 dependencies",
        "1 recorded attempt · up to 3 per run", "3 attempts recorded")]
    [InlineData(
        "zh-CN", 1,
        "1 个 Seeder 中已有 1 个进入终态", "正在显示 1 个 Seeder 中的 1 个", "1 个依赖项",
        "累计 1 次尝试 · 每次执行最多 1 次", "已记录 1 次")]
    [InlineData(
        "zh-CN", 3,
        "3 个 Seeder 中已有 1 个进入终态", "正在显示 3 个 Seeder 中的 1 个", "3 个依赖项",
        "累计 1 次尝试 · 每次执行最多 3 次", "已记录 3 次")]
    public void CountFormatters_WhenCountVaries_ShouldUseBilingualGrammar(
        string cultureName,
        int grammaticalCount,
        string expectedProgress,
        string expectedResults,
        string expectedDependencies,
        string expectedAttemptsSummary,
        string expectedRecordedAttempts)
    {
        using var culture = new CultureScope(cultureName);
        using var host = CreateLocalizationHost();
        var localizer = host.Services.GetRequiredService<IStringLocalizer<SeederResource>>();

        localizer.FormatSeederRunProgress(1, grammaticalCount).Should().Be(expectedProgress);
        localizer.FormatSeederFilterResults(1, grammaticalCount).Should().Be(expectedResults);
        localizer.FormatSeederDependencyCount(grammaticalCount).Should().Be(expectedDependencies);
        localizer.FormatSeederAttemptsSummary(1, grammaticalCount).Should().Be(expectedAttemptsSummary);
        localizer.FormatSeederRecordedAttempts(grammaticalCount).Should().Be(expectedRecordedAttempts);
    }

    private static IHost CreateLocalizationHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddLocalization().AddResource<SeederResource>();
        });
        return builder.Build();
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

        internal CultureScope(string cultureName)
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
        }
    }
}
