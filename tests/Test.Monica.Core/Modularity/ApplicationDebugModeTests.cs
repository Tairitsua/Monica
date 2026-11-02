using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Core.Modularity;

public sealed class ApplicationDebugModeTests
{
    [Theory]
    [InlineData("Development", null, false)]
    [InlineData("Production", null, false)]
    [InlineData("UAT", "true", true)]
    [InlineData("Production", "true", true)]
    [InlineData("Development", "false", false)]
    public async Task Composition_WhenDebugModeIsSelected_ShouldFreezeOnlyDiagnosticPolicy(string environment, string? input, bool expected)
    {
        var factory = new Factory(environment, input);
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var application = host.Services.GetRequiredService<MonicaApplication>();
        Assert.Equal(expected, application.Application.DebugMode);
        Assert.Same(application.Application, host.Services.GetRequiredService<IMonicaApplicationOptions>());
        Assert.True(application.Application.AutonomousWork);
        Assert.Throws<InvalidOperationException>(() => factory.Retained!.DebugMode = !expected);
        factory.Configuration!["Application:DebugMode"] = (!expected).ToString();
        Assert.Equal(expected, application.Application.DebugMode);
        var state = application.GetDebugModeState();
        Assert.Equal(expected, state.ActualValue);
        Assert.Equal(!expected, state.CandidateValue);
        Assert.True(state.RequiresRestart);
        Assert.Null(state.CandidateError);
    }

    [Theory]
    [InlineData("invalid", null)]
    [InlineData("true", false)]
    [InlineData("false", true)]
    public async Task Composition_WhenDebugInputIsInvalidOrConflicting_ShouldRejectHost(string input, bool? codeValue)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Factory("UAT", input, codeValue).CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Application:DebugMode", error.Message);
    }

    [Fact]
    public async Task Inspection_WhenCandidateIsInvalid_ShouldKeepActualAndReportFailure()
    {
        var factory = new Factory("UAT", "true");
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        factory.Configuration!["Application:DebugMode"] = "invalid";
        var state = host.Services.GetRequiredService<MonicaApplication>().GetDebugModeState();
        Assert.True(state.ActualValue);
        Assert.Null(state.CandidateValue);
        Assert.NotNull(state.CandidateError);
        Assert.True(state.RequiresRestart);
    }

    [Fact]
    public async Task Composition_WhenHostsUseDifferentPolicies_ShouldKeepCodeDefaultsAndSourcesIsolated()
    {
        await using var enabled = await new Factory("UAT", null, true).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var disabled = await new Factory("UAT", null).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var enabledState = enabled.Services.GetRequiredService<MonicaApplication>().GetDebugModeState();
        var disabledState = disabled.Services.GetRequiredService<MonicaApplication>().GetDebugModeState();
        Assert.True(enabledState.ActualValue);
        Assert.Equal("Code", enabledState.ActualSource);
        Assert.False(enabledState.RequiresRestart);
        Assert.False(disabledState.ActualValue);
        Assert.Equal("Default", disabledState.ActualSource);
    }

    private sealed class Factory(string environment, string? input, bool? codeValue = null)
        : MonicaTestApplicationFactory<ApplicationDebugModeTests>
    {
        internal ConfigurationManager? Configuration { get; private set; }
        internal MonicaApplicationOptions? Retained { get; private set; }

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            builder.Environment.EnvironmentName = environment;
            Configuration = builder.Configuration;
            if (input is not null) builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Application:DebugMode"] = input });
        }

        protected override void ConfigureMonica(IMonicaBuilder builder) => builder.ConfigureApplication(options =>
        {
            Retained = options;
            if (codeValue is { } value) options.DebugMode = value;
        });
    }
}
