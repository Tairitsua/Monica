using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.HealthCheck.UI.Localization;
using Monica.HealthCheck.UI.Pages;
using Monica.Modules;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
using Xunit;

namespace Test.Monica.HealthCheck.UI.Modules;

public sealed class ModuleHealthCheckUITests
{
    [Fact]
    public void Options_WhenNoPagePolicyIsConfigured_ShouldInheritShellPolicy()
    {
        var options = new ModuleHealthCheckUIOption();

        options.AuthorizationPolicyOverride.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectAndTransitiveComposition_ShouldPublishTheSameProtectedMonitorPage(
        bool useConvenienceEntry)
    {
        await using var app = Compose(
            configurePipeline: true,
            useConvenienceEntry: useConvenienceEntry);
        var catalog = app.Services.GetRequiredService<IPageCatalog>();
        var route = UIHealthCheckPage.PAGE_URL.Trim('/');
        var page = catalog.GetRegisteredPages().Single(definition => definition.Route == route);
        var navigation = catalog.GetNavItems().Single(item => item.Href == route);

        page.ComponentType.Should().Be(typeof(UIHealthCheckPage));
        page.DisplayName.ResourceType.Should().Be(typeof(HealthCheckResource));
        page.AccessPolicyType.Should().Be(typeof(OperationalPageAccessPolicy<ModuleHealthCheckUIOption>));
        navigation.Page.Should().BeSameAs(page);
        navigation.CategoryId.Should().Be(NavigationCategoryId.Create("Monica.HealthCheck"));
        catalog.GetNavigationCategories().Single(category => category.Id == navigation.CategoryId)
            .ParentId.Should().Be(BuiltInNavigationCategoryIds.Monitor);
        navigation.Order.Should().Be(10);
    }

    [Fact]
    public async Task Composition_ShouldRegisterSharedAccessPolicyAsScoped()
    {
        await using var app = Compose();
        using var firstScope = app.Services.CreateScope();
        using var secondScope = app.Services.CreateScope();

        var firstAccess = firstScope.ServiceProvider
            .GetRequiredService<OperationalPageAccessPolicy<ModuleHealthCheckUIOption>>();

        firstScope.ServiceProvider.GetRequiredService<OperationalPageAccessPolicy<ModuleHealthCheckUIOption>>()
            .Should().BeSameAs(firstAccess);
        secondScope.ServiceProvider.GetRequiredService<OperationalPageAccessPolicy<ModuleHealthCheckUIOption>>()
            .Should().NotBeSameAs(firstAccess);
    }

    private static WebApplication Compose(
        bool configurePipeline = false,
        bool useConvenienceEntry = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ModuleHealthCheckUI).Assembly.GetName().Name
        });
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options =>
            {
                options.ExcludeDefault();
                options.Add(
                    typeof(ModuleHealthCheckUI).Assembly,
                    typeof(ModuleHealthCheck).Assembly,
                    typeof(ModuleShellUI).Assembly);
            });
            if (useConvenienceEntry)
            {
                monica.AddHealthCheckUI();
            }
            else
            {
                monica.AddModule<HealthCheckUIConsumerModule, HealthCheckUIConsumerModuleOption>();
            }
        });

        var app = builder.Build();
        if (configurePipeline)
        {
            app.UseMonica();
            app.MapMonica();
        }

        return app;
    }
}

public sealed class HealthCheckUIConsumerModule : MonicaModule<HealthCheckUIConsumerModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleHealthCheckUI, ModuleHealthCheckUIOption>();
    }
}

public sealed class HealthCheckUIConsumerModuleOption : ModuleOptions<HealthCheckUIConsumerModule>;
