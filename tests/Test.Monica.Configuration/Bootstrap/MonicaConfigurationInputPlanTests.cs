using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.Models;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Configuration.Bootstrap;

public sealed class MonicaConfigurationInputPlanTests
{
    [Fact]
    public void Create_WhenStoreIsMissing_ShouldRejectPlan()
    {
        Action create = () => MonicaConfigurationInputPlan.Create(inputs => inputs
            .AddManagedJsonFile("appsettings.json"));

        create.Should().Throw<InvalidOperationException>()
            .WithMessage("*exactly one store composition*");
    }

    [Fact]
    public void Create_WhenSecondStoreIsSelected_ShouldRejectAmbiguousComposition()
    {
        var first = new RecordingStoreComposition("First");
        var second = new RecordingStoreComposition("Second");

        Action create = () => MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(first)
            .UseConfigurationStore(second));

        create.Should().Throw<InvalidOperationException>()
            .WithMessage("*First*Second*");
    }

    [Fact]
    public void Create_WhenManagedJsonPathDiffersOnlyByCase_ShouldRejectDuplicateSource()
    {
        var composition = new RecordingStoreComposition("Recording");

        Action create = () => MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition)
            .AddManagedJsonFile("settings.json")
            .AddManagedJsonFile("SETTINGS.JSON"));

        create.Should().Throw<InvalidOperationException>()
            .WithMessage("*already declared*");
    }

    [Fact]
    public void Create_WhenSectionPathConventionIsDeclaredTwice_ShouldRejectAmbiguousConvention()
    {
        var composition = new RecordingStoreComposition("Recording");

        Action create = () => MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition)
            .UseSectionPathConvention(ConfigurationSectionPathConvention.ClrFullName)
            .UseSectionPathConvention(ConfigurationSectionPathConvention.ShortTypeName));

        create.Should().Throw<InvalidOperationException>()
            .WithMessage("*already declared*");
    }

    [Fact]
    public void Create_WhenDeclarationBuilderEscapes_ShouldSealBuilder()
    {
        MonicaConfigurationInputPlanBuilder? declaration = null;
        _ = MonicaConfigurationInputPlan.Create(inputs =>
        {
            declaration = inputs;
            inputs.UseConfigurationStore(new RecordingStoreComposition("Recording"));
        });

        Action mutate = () => declaration!.AddManagedJsonFile("late.json");

        mutate.Should().Throw<InvalidOperationException>()
            .WithMessage("*sealed*");
    }

    [Fact]
    public async Task AddConfiguration_WhenPlansBelongToDifferentHosts_ShouldKeepSourcesIsolatedAndStoreLazy()
    {
        using var directory = new TemporaryConfigurationDirectory();
        await directory.WriteJsonAsync("first.json", """{"InputPlan":{"Value":"first","FirstOnly":"yes"}}""");
        await directory.WriteJsonAsync("second.json", """{"InputPlan":{"Value":"second","SecondOnly":"yes"}}""");
        var firstStore = Path.Combine(directory.RootPath, "first-store");
        var secondStore = Path.Combine(directory.RootPath, "second-store");
        var firstPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseFileConfigurationStore(options => options.RootDirectory = firstStore)
            .AddManagedJsonFile("first.json", optional: false, reloadOnChange: false));
        var secondPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseFileConfigurationStore(options => options.RootDirectory = secondStore)
            .AddManagedJsonFile("second.json", optional: false, reloadOnChange: false));
        var firstBuilder = directory.CreateHostBuilder();
        var secondBuilder = directory.CreateHostBuilder();
        firstBuilder.Configuration["InputPlan:Value"] = "host-first";
        secondBuilder.Configuration["InputPlan:Value"] = "host-second";
        firstBuilder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddConfiguration(firstPlan);
        });
        secondBuilder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddConfiguration(secondPlan);
        });
        using var first = firstBuilder.Build();
        using var second = secondBuilder.Build();

        firstBuilder.Configuration["InputPlan:Value"].Should().Be("first");
        firstBuilder.Configuration["InputPlan:SecondOnly"].Should().BeNull();
        secondBuilder.Configuration["InputPlan:Value"].Should().Be("second");
        secondBuilder.Configuration["InputPlan:FirstOnly"].Should().BeNull();
        Directory.Exists(firstStore).Should().BeFalse();
        Directory.Exists(secondStore).Should().BeFalse();
    }

    private sealed class RecordingStoreComposition(string name) : IMonicaConfigurationStoreComposition
    {
        public string Name { get; } = name;
        public void ConfigureRuntime(ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> module) { }
    }

    private sealed class TemporaryConfigurationDirectory : IDisposable
    {
        public TemporaryConfigurationDirectory()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"monica-input-plan-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }
        public string RootPath { get; }
        public HostApplicationBuilder CreateHostBuilder() => new(new HostApplicationBuilderSettings
        {
            ContentRootPath = RootPath,
            DisableDefaults = true
        });
        public Task WriteJsonAsync(string path, string json) => File.WriteAllTextAsync(
            Path.Combine(RootPath, path), json, TestContext.Current.CancellationToken);
        public void Dispose() => Directory.Delete(RootPath, recursive: true);
    }
}
