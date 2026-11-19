using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Exceptions;
using Monica.Core.Modularity.Extensions;
using Monica.Framework.UI.UIExecutionPipeline.State;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Framework.UI.ExecutionPipeline;

public sealed class ModuleExecutionPipelineUITests
{
    [Fact]
    public void Module_ShouldRemainNonWeb()
    {
        var module = new ModuleExecutionPipelineUI();

        module.Should().BeAssignableTo<IModule>();
        module.Should().BeAssignableTo<IUIModule>();
        module.Should().NotBeAssignableTo<IWebModule>();
    }

    [Fact]
    public async Task Composition_ShouldRegisterScopedPageState()
    {
        await using var app = Compose(useConvenienceEntry: false);
        using var firstScope = app.Services.CreateScope();
        using var secondScope = app.Services.CreateScope();

        firstScope.ServiceProvider.GetRequiredService<ExecutionPipelinePageState>().Should()
            .NotBeSameAs(secondScope.ServiceProvider.GetRequiredService<ExecutionPipelinePageState>());
    }

    [Fact]
    public async Task AddExecutionPipelineUI_ShouldComposeRuntimeAndPageDependencies()
    {
        await using var app = Compose(useConvenienceEntry: true);
        var application = app.Services.GetRequiredService<MonicaApplication>();
        var dependencies = GetDirectDependencyTypes(application, typeof(ModuleExecutionPipelineUI));

        dependencies.Should().Contain(typeof(ModuleExecutionPipeline));
        dependencies.Should().Contain(typeof(ModuleLocalization));
        dependencies.Should().Contain(typeof(ModuleShellUI));
    }

    [Fact]
    public async Task DirectAndTransitiveComposition_ShouldDeclareEquivalentIntrinsicDependencies()
    {
        await using var directApp = Compose(useConvenienceEntry: true);
        await using var transitiveApp = Compose(useConvenienceEntry: false);
        var directDependencies = GetDirectDependencyTypes(
            directApp.Services.GetRequiredService<MonicaApplication>(),
            typeof(ModuleExecutionPipelineUI));
        var transitiveDependencies = GetDirectDependencyTypes(
            transitiveApp.Services.GetRequiredService<MonicaApplication>(),
            typeof(ModuleExecutionPipelineUI));

        transitiveDependencies.Should().BeEquivalentTo(directDependencies);
        transitiveDependencies.Should().Contain(typeof(ModuleExecutionPipeline));
        transitiveDependencies.Should().Contain(typeof(ModuleLocalization));
        transitiveDependencies.Should().Contain(typeof(ModuleShellUI));
    }

    [Fact]
    public void GenericHost_WhenLeafUiIsIncludedTransitively_ShouldAttributeHostRequirementToShell()
    {
        var builder = Host.CreateApplicationBuilder();

        Action compose = () => builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddModule<ExecutionPipelineUIConsumerModule, ExecutionPipelineUIConsumerModuleOption>();
        });

        compose.Should().Throw<ModuleRegistrationException>()
            .WithMessage($"*WebApplicationBuilder*{nameof(ModuleShellUI)}*");
    }

    private static WebApplication Compose(bool useConvenienceEntry)
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options =>
            {
                options.ExcludeDefault();
                options.Add(
                    typeof(ModuleExecutionPipelineUI).Assembly,
                    typeof(ModuleExecutionPipeline).Assembly,
                    typeof(ModuleShellUI).Assembly);
            });
            if (useConvenienceEntry)
            {
                monica.AddExecutionPipelineUI();
            }
            else
            {
                monica.AddModule<ExecutionPipelineUIConsumerModule, ExecutionPipelineUIConsumerModuleOption>();
            }
        });

        return builder.Build();
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

public sealed class ExecutionPipelineUIConsumerModule
    : MonicaModule<ExecutionPipelineUIConsumerModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleExecutionPipelineUI>();
    }
}

public sealed class ExecutionPipelineUIConsumerModuleOption
    : ModuleOptions<ExecutionPipelineUIConsumerModule>;
