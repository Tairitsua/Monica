using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.UI.Hosting;

public sealed class ModuleUIHostDebugModeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Composition_WhenDebugModeChanges_ShouldConfigureCircuitAndServerRendering(bool enabled)
    {
        await using var application = await new Factory(enabled).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(enabled, application.Services.GetRequiredService<IOptions<CircuitOptions>>().Value.DetailedErrors);
        Assert.Equal(enabled, application.Services.GetRequiredService<IOptions<RazorComponentsServiceOptions>>().Value.DetailedErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_WhenNativeDetailedErrorsContradictPolicy_ShouldFailClearly(bool serverRendering)
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            new Factory(false, serverRendering).CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Application:DebugMode", error.Message);
    }

    private sealed class Factory(bool enabled, bool? serverRenderingConflict = null) : MonicaTestApplicationFactory<ModuleUIHostDebugModeTests>
    {
        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            builder.Environment.EnvironmentName = "Production";
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DetailedErrors"] = (!enabled).ToString()
            });
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => options.DebugMode = enabled);
            builder.AddUIHost();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            if (serverRenderingConflict == true)
                services.PostConfigure<RazorComponentsServiceOptions>(options => options.DetailedErrors = !enabled);
            else if (serverRenderingConflict == false)
                services.Configure<CircuitOptions>(options => options.DetailedErrors = !enabled);
        }
    }
}
