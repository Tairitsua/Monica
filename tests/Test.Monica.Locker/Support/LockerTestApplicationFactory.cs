using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Abstractions;
using Monica.Testing.Hosting;

namespace Test.Monica.Locker.Support;

internal sealed class LockerTestApplicationFactory(
    Action<IMonicaBuilder> compose,
    Action<IServiceCollection>? configureServices = null)
    : MonicaTestApplicationFactory<LockerTestApplicationFactory>
{
    protected override void ConfigureHost(WebApplicationBuilder builder)
    {
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
    }

    protected override void ConfigureMonica(IMonicaBuilder builder)
    {
        builder.ConfigureModuleSystem(options =>
        {
            options.AutoAddMonicaHttpListener = false;
            options.EnableMinimalApiByDefault = false;
        });
        compose(builder);
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        configureServices?.Invoke(services);
    }
}
