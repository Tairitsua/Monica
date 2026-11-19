using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.UI.Pages;
using Monica.Modules;
using Monica.UI.Shell.Support;
using Xunit;

namespace Test.Monica.JobScheduler.UI.Modules;

public sealed class ModuleJobSchedulerUITests
{
    [Fact]
    public void Options_WhenNoPagePolicyIsConfigured_ShouldInheritShellPolicy()
    {
        new ModuleJobSchedulerUIOption().AuthorizationPolicyOverride.Should().BeNull();
    }

    [Fact]
    public async Task AddJobSchedulerUi_ShouldComposeOnlyItsRuntimeLocalizationAndShellDependencies()
    {
        await using var host = ComposeJobSchedulerUi(useConvenienceEntry: true);
        var application = host.Services.GetRequiredService<MonicaApplication>();
        var dependencies = GetDirectDependencyTypes(application, typeof(ModuleJobSchedulerUI));

        dependencies.Should().Contain(typeof(ModuleLocalization));
        dependencies.Should().Contain(typeof(ModuleJobScheduler));
        dependencies.Should().Contain(typeof(ModuleShellUI));
        dependencies.Should().NotContain(typeof(ModuleStackTraceUI));
    }

    [Fact]
    public async Task DirectAndTransitiveComposition_ShouldDeclareEquivalentIntrinsicDependencies()
    {
        await using var directHost = ComposeJobSchedulerUi(useConvenienceEntry: true);
        await using var transitiveHost = ComposeJobSchedulerUi(useConvenienceEntry: false);
        var directDependencies = GetDirectDependencyTypes(
            directHost.Services.GetRequiredService<MonicaApplication>(),
            typeof(ModuleJobSchedulerUI));
        var transitiveDependencies = GetDirectDependencyTypes(
            transitiveHost.Services.GetRequiredService<MonicaApplication>(),
            typeof(ModuleJobSchedulerUI));

        transitiveDependencies.Should().BeEquivalentTo(directDependencies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Composition_ShouldProtectEverySchedulerRoute(bool useConvenienceEntry)
    {
        await using var app = ComposeJobSchedulerUi(useConvenienceEntry, configurePipeline: true);
        var pages = app.Services.GetRequiredService<IPageCatalog>().GetRegisteredPages()
            .Where(page => page.ComponentType == typeof(SchedulerOverviewPage)
                           || page.ComponentType == typeof(JobCatalogPage)
                           || page.ComponentType == typeof(JobDefinitionDetailPage)
                           || page.ComponentType == typeof(JobExecutionsPage))
            .ToArray();

        pages.Should().HaveCount(4);
        pages.Should().OnlyContain(page =>
            page.AccessPolicyType == typeof(OperationalPageAccessPolicy<ModuleJobSchedulerUIOption>));
    }

    [Fact]
    public async Task Composition_ShouldRegisterSchedulerAccessPolicyAsScoped()
    {
        await using var app = ComposeJobSchedulerUi(useConvenienceEntry: true);
        using var firstScope = app.Services.CreateScope();
        using var secondScope = app.Services.CreateScope();
        var first = firstScope.ServiceProvider
            .GetRequiredService<OperationalPageAccessPolicy<ModuleJobSchedulerUIOption>>();

        firstScope.ServiceProvider
            .GetRequiredService<OperationalPageAccessPolicy<ModuleJobSchedulerUIOption>>()
            .Should().BeSameAs(first);
        secondScope.ServiceProvider
            .GetRequiredService<OperationalPageAccessPolicy<ModuleJobSchedulerUIOption>>()
            .Should().NotBeSameAs(first);
    }

    private static WebApplication ComposeJobSchedulerUi(bool useConvenienceEntry, bool configurePipeline = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ModuleJobSchedulerUI).Assembly.GetName().Name
        });
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options =>
            {
                options.ExcludeDefault();
                options.Add(
                    typeof(ModuleJobSchedulerUI).Assembly,
                    typeof(ModuleJobScheduler).Assembly,
                    typeof(ModuleShellUI).Assembly);
            });
            monica.AddJobScheduler()
                .UseInMemoryStore()
                .UseSchedulerScope("job-ui-tests");
            if (useConvenienceEntry)
            {
                monica.AddJobSchedulerUI();
            }
            else
            {
                monica.AddModule<JobSchedulerUIConsumerModule, JobSchedulerUIConsumerModuleOption>();
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

public sealed class JobSchedulerUIConsumerModule : MonicaModule<JobSchedulerUIConsumerModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleJobSchedulerUI>();
    }
}

public sealed class JobSchedulerUIConsumerModuleOption : ModuleOptions<JobSchedulerUIConsumerModule>;
