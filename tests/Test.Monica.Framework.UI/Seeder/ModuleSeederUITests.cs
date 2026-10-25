using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.Pages;
using Monica.Framework.UI.UISeeder.State;
using Monica.Modules;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
using Xunit;

namespace Test.Monica.Framework.UI.Seeder;

public sealed class ModuleSeederUITests
{
    [Fact]
    public void Module_ShouldRemainNonWeb()
    {
        var module = new ModuleSeederUI();

        module.Should().BeAssignableTo<IModule>();
        module.Should().BeAssignableTo<IUIModule>();
        module.Should().NotBeAssignableTo<IWebModule>();
    }

    [Fact]
    public void Options_WhenNoPagePolicyIsConfigured_ShouldInheritShellPolicy()
    {
        var options = new ModuleSeederUIOption();

        options.AuthorizationPolicyOverride.Should().BeNull();
    }

    [Fact]
    public void Options_WhenPagePolicyIsConfigured_ShouldRetainOverride()
    {
        var options = new ModuleSeederUIOption
        {
            AuthorizationPolicyOverride = "SeederDiagnostics"
        };

        options.AuthorizationPolicyOverride.Should().Be("SeederDiagnostics");
    }

    [Fact]
    public async Task AddSeederUI_ShouldComposeRuntimeAndPageDependencies()
    {
        await using var app = Compose();
        var application = app.Services.GetRequiredService<MonicaApplication>();
        var dependencies = GetDirectDependencyTypes(application, typeof(ModuleSeederUI));

        dependencies.Should().Contain(typeof(ModuleSeeder));
        dependencies.Should().Contain(typeof(ModuleLocalization));
        dependencies.Should().Contain(typeof(ModuleShellUI));
    }

    [Fact]
    public async Task Composition_ShouldRegisterScopedAccessAndSingletonSessionFactory()
    {
        await using var app = Compose();
        using var firstScope = app.Services.CreateScope();
        using var secondScope = app.Services.CreateScope();

        var firstAccess = firstScope.ServiceProvider
            .GetRequiredService<OperationalPageAccessPolicy<ModuleSeederUIOption>>();
        firstScope.ServiceProvider.GetRequiredService<OperationalPageAccessPolicy<ModuleSeederUIOption>>()
            .Should().BeSameAs(firstAccess);
        secondScope.ServiceProvider.GetRequiredService<OperationalPageAccessPolicy<ModuleSeederUIOption>>()
            .Should().NotBeSameAs(firstAccess);

        var firstFactory = firstScope.ServiceProvider.GetRequiredService<SeederPageSessionFactory>();
        secondScope.ServiceProvider.GetRequiredService<SeederPageSessionFactory>().Should().BeSameAs(firstFactory);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectAndTransitiveComposition_ShouldPublishTheSameProtectedMonitorNavigation(
        bool useConvenienceEntry)
    {
        await using var app = Compose(
            configurePipeline: true,
            useConvenienceEntry: useConvenienceEntry);
        var catalog = app.Services.GetRequiredService<IPageCatalog>();
        var route = UISeederPage.PAGE_URL.Trim('/');
        var page = catalog.GetRegisteredPages().Single(definition => definition.Route == route);
        var navigation = catalog.GetNavItems().Single(item => item.Href == route);

        page.ComponentType.Should().Be(typeof(UISeederPage));
        page.DisplayName.ResourceType.Should().Be(typeof(SeederResource));
        page.DisplayName.Key.Should().Be("Navigation:Title");
        page.AccessPolicyType.Should().Be(typeof(OperationalPageAccessPolicy<ModuleSeederUIOption>));
        navigation.Page.Should().BeSameAs(page);
        navigation.CategoryId.Should().Be(NavigationCategoryId.Create("Monica.Framework.UI.Seeder"));
        catalog.GetNavigationCategories().Single(category => category.Id == navigation.CategoryId)
            .ParentId.Should().Be(BuiltInNavigationCategoryIds.Monitor);
        navigation.Order.Should().Be(15);
    }

    private static WebApplication Compose(
        bool configurePipeline = false,
        bool useConvenienceEntry = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ModuleSeederUI).Assembly.GetName().Name
        });
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options =>
            {
                options.ExcludeDefault();
                options.Add(
                    typeof(ModuleSeederUI).Assembly,
                    typeof(ModuleSeeder).Assembly,
                    typeof(ModuleShellUI).Assembly);
            });
            if (useConvenienceEntry)
            {
                monica.AddSeederUI();
            }
            else
            {
                monica.AddModule<SeederUIConsumerModule, SeederUIConsumerModuleOption>();
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

    private static IReadOnlySet<Type> GetDirectDependencyTypes(
        MonicaApplication application,
        Type moduleType)
    {
        var moduleKey = application.Dependencies.ModuleKeysByType[moduleType];
        return application.Dependencies.DependenciesByModule[moduleKey]
            .Select(dependency => application.Dependencies.ModuleTypesByKey[dependency])
            .ToHashSet();
    }
}

public sealed class SeederUIConsumerModule : MonicaModule<SeederUIConsumerModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleSeederUI, ModuleSeederUIOption>();
    }
}

public sealed class SeederUIConsumerModuleOption : ModuleOptions<SeederUIConsumerModule>;
