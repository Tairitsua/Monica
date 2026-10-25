using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.Profiling.Localization;
using Monica.Profiling.Pages;
using Monica.Testing.Localization;
using MudBlazor.Services;
using Xunit;

namespace Test.Monica.Profiling.Pages;

public sealed class OptionalProfilingPageTests
{
    [Fact]
    public void RuntimeMetrics_WhenOwningModuleIsDisabled_ShouldRenderUnavailableState()
    {
        using var context = CreateContext();

        var page = context.Render<UIRuntimeMetricsPage>();

        page.Find("[data-testid='profiling-module-unavailable']")
            .GetAttribute("role").Should().Be("status");
        page.Markup.Should().Contain("Unavailable:RuntimeMetrics:Title");
        page.Markup.Should().Contain("Unavailable:RuntimeMetrics:Description");
    }

    [Fact]
    public void MemoryAnalysis_WhenOwningModuleIsDisabled_ShouldRenderUnavailableState()
    {
        using var context = CreateContext();

        var page = context.Render<UIMemoryAnalysisPage>();

        page.Find("[data-testid='profiling-module-unavailable']")
            .GetAttribute("role").Should().Be("status");
        page.Markup.Should().Contain("Unavailable:MemoryAnalysis:Title");
        page.Markup.Should().Contain("Unavailable:MemoryAnalysis:Description");
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.Services.AddOptions<global::Monica.Modules.ModuleShellUIOption>();
        context.Services.AddOptions<global::Monica.Modules.ModuleClockOption>();
        context.Services.AddSingleton<global::Monica.UI.Shared.Services.MoTimeFormatter>();
        context.Services.AddSingleton<IStringLocalizer<RuntimeMetricsResource>,
            EchoStringLocalizer<RuntimeMetricsResource>>();
        context.Services.AddSingleton<IStringLocalizer<MemoryAnalysisResource>,
            EchoStringLocalizer<MemoryAnalysisResource>>();
        context.Services.AddSingleton<IStringLocalizer<ProfilingResource>,
            EchoStringLocalizer<ProfilingResource>>();
        return context;
    }
}
