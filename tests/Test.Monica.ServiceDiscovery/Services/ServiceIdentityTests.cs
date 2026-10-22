using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Modules;
using Monica.ServiceDiscovery.Providers;
using Xunit;

namespace Test.Monica.ServiceDiscovery.Services;

public sealed class ServiceIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("another-global-app")]
    public void Registration_WithoutDisplayName_UsesItsResolvedAppId(string? globalAppId)
    {
        var module = new ModuleServiceDiscoveryOption { AppId = "flight-api" };
        var application = new MonicaApplicationOptions { AppId = globalAppId, ProjectName = "Product.Host" };
        var client = new DefaultServiceDiscoveryClientInfo(Options.Create(module), application);
        Assert.Equal("flight-api", client.GetServiceStatus().AppName);
        application.AppName = "Product label";
        Assert.Equal("Product label", new DefaultServiceDiscoveryClientInfo(Options.Create(module), application).GetServiceStatus().AppName);
        module.AppName = "Flight service";
        Assert.Equal("Flight service", new DefaultServiceDiscoveryClientInfo(Options.Create(module), application).GetServiceStatus().AppName);
    }
}
