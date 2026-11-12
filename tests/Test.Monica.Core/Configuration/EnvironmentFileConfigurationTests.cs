using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Configuration;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.Configuration;

public sealed class EnvironmentFileConfigurationTests
{
    [Fact]
    public void AddMonica_WhenLoadedProvidersHaveProgrammaticValues_ShouldPreserveThemAcrossFileInsertion()
    {
        using var files = new InputFiles();
        files.Write("appsettings.json", """{"Application":{"EnvironmentFiles":["missing.json"]},"Settings":{"Value":"file","Items":["file-item"]}}""");
        files.Write("site.json", """{"Connection":{"Name":"selected-site"}}""");
        var builder = files.CreateBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", false, false);
        builder.Configuration["Application:EnvironmentFiles:0"] = "site.json";
        builder.Configuration["Settings:Value"] = "assigned-before-composition";
        builder.Configuration["Settings:Items"] = null;
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddModuleSystem();
            var snapshot = monica.Composition.StartupConfiguration;
            snapshot["Settings:Value"].Should().Be("assigned-before-composition");
            snapshot["Application:EnvironmentFiles:0"].Should().Be("site.json");
            snapshot["Connection:Name"].Should().Be("selected-site");
            new ConfigurationShapeView(snapshot).TryGetShape("Settings:Items", out var shape).Should().BeTrue();
            shape.Should().Be(ConfigurationValueShape.Null);
            snapshot.GetSection("Settings:Items").GetChildren().Should().BeEmpty();
        });
        using var host = builder.Build();
    }

    [Fact]
    public void AddMonica_WhenOneFileOverridesALongerList_ShouldReplaceTheSelectionAndRespectPriority()
    {
        using var files = new InputFiles();
        files.Write("appsettings.json", """{"Application":{"EnvironmentFiles":["missing-one.json","missing-two.json","missing-three.json"]},"Settings":{"Value":"base","FromEnvironment":"base"}}""");
        files.Write("appsettings.Development.json", """{"Settings":{"Value":"development"}}""");
        files.Write("site.json", """{"Settings":{"Value":"site","FromEnvironment":"site","Empty":[]}}""");
        var builder = files.CreateBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", false, false);
        builder.Configuration.AddJsonFile("appsettings.Development.json", false, false);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Application:EnvironmentFiles:0"] = "site.json", ["Settings:Value"] = "secrets"
        });
        builder.Configuration.AddCommandLine(["--Settings:Value=command-line"]);

        MonicaCompositionState? captured = null;
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddModuleSystem();
            captured = monica.Composition;
            captured.StartupConfiguration["Settings:FromEnvironment"].Should().Be("site");
            captured.StartupConfiguration["Settings:Value"].Should().Be("command-line");
            var selected = captured.StartupConfiguration.SelectCompleteSection("Application:EnvironmentFiles");
            selected!.GetSection("Application:EnvironmentFiles").GetChildren().Should().ContainSingle();
        });
        using var host = builder.Build();
        var composition = host.Services.GetRequiredService<MonicaApplication>().Composition;
        composition.Should().BeSameAs(captured);
        composition.EnvironmentFileDiagnostics.Should().ContainSingle();
        new ConfigurationShapeView(composition.StartupConfiguration).TryGetShape("Settings:Empty", out var shape).Should().BeTrue();
        shape.Should().Be(ConfigurationValueShape.List);
        var origins = composition.StartupConfiguration.Contributions.Select(provider => provider.Origin).ToArray();
        var environment = origins.Single(origin => origin.PhysicalPath == files.PathFor("site.json"));
        var development = origins.Single(origin => origin.SourcePath == "appsettings.Development.json");
        environment.PriorityIndex.Should().Be(development.PriorityIndex + 1);
    }

    [Fact]
    public void AddMonica_WhenDefaultJsonContainsEmptyContainers_ShouldFreezeTheirOriginalShapes()
    {
        using var files = new InputFiles();
        files.Write("appsettings.json", """{"Settings":{"Object":{},"List":[],"Null":null},"Application":{"EnvironmentFiles":[]}}""");
        var builder = files.CreateBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", false, false);
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddModuleSystem();
            var view = new ConfigurationShapeView(monica.Composition.StartupConfiguration);
            view.TryGetShape("Settings:Object", out var objectShape).Should().BeTrue();
            objectShape.Should().Be(ConfigurationValueShape.Object);
            view.TryGetShape("Settings:List", out var listShape).Should().BeTrue();
            listShape.Should().Be(ConfigurationValueShape.List);
            view.TryGetShape("Settings:Null", out var nullShape).Should().BeTrue();
            nullShape.Should().Be(ConfigurationValueShape.Null);
        });
        using var host = builder.Build();
    }

    [Fact]
    public void Reload_WhenCandidateIsRejected_ShouldRetainValuesAndShapesUntilAValidCandidateArrives()
    {
        using var files = new InputFiles();
        files.Write("appsettings.json", """{"Application":{"EnvironmentFiles":["site.json"]}}""");
        files.Write("site.json", """{"Connection":{"Name":"initial","Items":[]}}""");
        var builder = files.CreateBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", false, false);
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddModuleSystem();
            monica.Composition.OnServicesFinalized(_ => monica.Composition.ValidateEnvironmentFiles(snapshot =>
            {
                if (snapshot.Contains("Managed") || ConfigurationShapeView.HasBarrier(snapshot, "Managed"))
                    throw new ConfigurationSourcePathException("Managed");
            }));
        });
        using var host = builder.Build();
        var composition = host.Services.GetRequiredService<MonicaApplication>().Composition;
        var provider = ((IConfigurationRoot)builder.Configuration).Providers.OfType<FileConfigurationProvider>()
            .Single(item => item.Source.Path == "site.json");
        Load(provider, """{"Connection":{"Name":"candidate","Items":["changed"]},"Managed":null}""");
        builder.Configuration["Connection:Name"].Should().Be("initial");
        new ConfigurationShapeView(builder.Configuration).TryGetShape("Connection:Items", out var heldShape).Should().BeTrue();
        heldShape.Should().Be(ConfigurationValueShape.List);
        builder.Configuration.GetSection("Connection:Items").GetChildren().Should().BeEmpty();
        composition.EnvironmentFileDiagnostics.Single().Error.Should().Contain("Managed");

        Load(provider, "{ malformed SECRET-CANDIDATE");
        builder.Configuration["Connection:Name"].Should().Be("initial");
        composition.EnvironmentFileDiagnostics.Single().Error.Should().NotContain("SECRET-CANDIDATE");
        File.Delete(files.PathFor("site.json"));
        provider.Load();
        builder.Configuration["Connection:Name"].Should().Be("initial");
        composition.EnvironmentFileDiagnostics.Single().Error.Should().NotBeNull();

        Load(provider, """{"Connection":{"Name":"accepted","Items":["new"]}}""");
        builder.Configuration["Connection:Name"].Should().Be("accepted");
        composition.StartupConfiguration["Connection:Name"].Should().Be("initial");
        composition.EnvironmentFileDiagnostics.Single().Error.Should().BeNull();
    }

    [Theory]
    [InlineData("{\"Application\":{\"EnvironmentFiles\":[]}}")]
    [InlineData("{\"Application\":null}")]
    [InlineData("{\"Application\":{}}")]
    public void AddMonica_WhenAnEnvironmentFileRedefinesTheSelection_ShouldRejectIt(string content)
    {
        using var files = new InputFiles();
        files.Write("appsettings.json", """{"Application":{"EnvironmentFiles":["site.json"]}}""");
        files.Write("site.json", content);
        var builder = files.CreateBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", false, false);
        Action compose = () => builder.AddMonica(_ => { });
        compose.Should().Throw<InvalidDataException>();
        ((IDisposable)builder.Configuration).Dispose();
    }

    [Fact]
    public void AddMonica_WhenARequiredFileIsMissing_ShouldFailBeforeTheCallback()
    {
        using var files = new InputFiles();
        files.Write("appsettings.json", """{"Application":{"EnvironmentFiles":["missing.json"]}}""");
        var builder = files.CreateBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", false, false);
        var called = false;
        Action compose = () => builder.AddMonica(_ => called = true);
        compose.Should().Throw<FileNotFoundException>();
        called.Should().BeFalse();
        ((IDisposable)builder.Configuration).Dispose();
    }

    [Fact]
    public void AddMonica_WhenASelectedListIsExplicitNull_ShouldNotUseTheLowerList()
    {
        using var files = new InputFiles();
        files.Write("appsettings.json", """{"Application":{"EnvironmentFiles":["missing.json"]}}""");
        var builder = files.CreateBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", false, false);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Application:EnvironmentFiles"] = null });
        Action compose = () => builder.AddMonica(_ => { });
        compose.Should().Throw<InvalidOperationException>().WithMessage("*complete list*");
        ((IDisposable)builder.Configuration).Dispose();
    }

    [Fact]
    public void AddMonica_WhenAHostHasCustomSources_ShouldRequireAndHonorTheExplicitBoundary()
    {
        using var files = new InputFiles();
        files.Write("site.json", """{"Settings":{"Value":"site"}}""");
        var builder = files.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Application:EnvironmentFiles:0"] = "site.json" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Settings:Value"] = "higher" });
        Action withoutBoundary = () => builder.AddMonica(_ => { });
        withoutBoundary.Should().Throw<InvalidOperationException>().WithMessage("*SetMonicaEnvironmentFileInsertionIndex*");

        builder.SetMonicaEnvironmentFileInsertionIndex(builder.Configuration.Sources.Count - 1);
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddModuleSystem();
            monica.Composition.StartupConfiguration["Settings:Value"].Should().Be("higher");
        });
        using var host = builder.Build();
    }

    private static void Load(FileConfigurationProvider provider, string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        provider.Load(stream);
    }

    private sealed class InputFiles : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"monica-startup-{Guid.NewGuid():N}");
        internal InputFiles() => Directory.CreateDirectory(_root);
        internal string PathFor(string path) => System.IO.Path.Combine(_root, path);
        internal void Write(string path, string text) => File.WriteAllText(PathFor(path), text);
        internal HostApplicationBuilder CreateBuilder()
        {
            var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
            {
                DisableDefaults = true, ContentRootPath = _root, EnvironmentName = "Development"
            });
            builder.Configuration.SetBasePath(_root);
            return builder;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
