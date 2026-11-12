using System.Reflection;
using System.Reflection.Emit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.Annotations;
using Monica.Configuration.Facades;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Configuration.Bootstrap;

public sealed class ApplicationDebugModeProjectionTests
{
    [Fact]
    public async Task Inspection_WhenNoStoreIsConfigured_ShouldProjectCoreActualAndRestartCandidate()
    {
        var factory = new Factory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var facade = application.Services.GetRequiredService<ConfigurationFacade>();
        var actual = facade.GetDebugModeState();
        Assert.True(actual.IsSuccess());
        Assert.True(actual.Data!.ActualValue);
        Assert.Equal(application.Services.GetRequiredService<IMonicaApplicationOptions>().DebugMode, actual.Data.ActualValue);
        factory.Configuration!["Application:DebugMode"] = "false";
        var candidate = facade.GetDebugModeState();
        Assert.True(candidate.IsSuccess());
        Assert.True(candidate.Data!.ActualValue);
        Assert.False(candidate.Data.CandidateValue);
        Assert.True(candidate.Data.RequiresRestart);
    }

    [Theory]
    [InlineData("Application")]
    [InlineData("Application:DebugMode")]
    public async Task Composition_WhenManagedDefinitionOverlapsDebugPolicy_ShouldRejectCompetingOwner(string section)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"DebugPolicyOverlap{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("main").DefineType("ConflictingOptions", TypeAttributes.Public);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        type.SetCustomAttribute(new CustomAttributeBuilder(typeof(ConfigurationAttribute).GetConstructor([typeof(string)])!, [section]));
        type.CreateType();
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            new Factory(assembly).CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Application:DebugMode", error.ToString());
    }

    private sealed class Factory(Assembly? definitions = null) : MonicaTestApplicationFactory<ApplicationDebugModeProjectionTests>
    {
        internal ConfigurationManager? Configuration { get; private set; }
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => [definitions ?? typeof(ModuleConfiguration).Assembly];

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            Configuration = builder.Configuration;
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Application:DebugMode"] = "true" });
        }

        protected override void ConfigureMonica(IMonicaBuilder builder) => builder.AddConfiguration();
    }
}
