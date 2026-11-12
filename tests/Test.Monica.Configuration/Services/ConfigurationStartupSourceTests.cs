using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Configuration.Annotations;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Configuration;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class ConfigurationStartupSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectStartup_WhenDeploymentChanges_ShouldPreserveFrozenProvenanceAndMaskNestedSecrets(bool environmentFile)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"monica-startup-sources-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deployment.json");
        try
        {
            await File.WriteAllTextAsync(path,
                """{"StartupSources":{"Name":"original","Credentials":{"main":{"Token":"dictionary-secret"}},"Items":[{"Token":"list-secret"}]}}""",
                TestContext.Current.CancellationToken);
            var builder = Host.CreateApplicationBuilder();
            using var configuration = builder.Configuration;
            configuration.Sources.Clear();
            if (environmentFile)
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Application:EnvironmentFiles:0"] = path });
                builder.SetMonicaEnvironmentFileInsertionIndex(0);
            }
            else configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
            ConfigurationStartupOptionsReader reader = null!;
            builder.AddMonica(monica =>
            {
                monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
                monica.GetStartupOptions<StartupSourceOptions>();
                reader = ConfigurationStartupOptionsReader.For(monica.Composition);
            });
            var definition = new ConfigurationDefinitionScanner(new ConfigurationSchemaHasher()).Scan(typeof(StartupSourceOptions));
            var context = new ConfigurationRuntimeContext { StartupOptions = reader };
            context.Capture(configuration);
            var registry = new ConfigurationDefinitionRegistry();
            registry.Register(definition);
            var inspector = new ConfigurationSourceInspector(context, registry,
                new ConfigurationPathProjector(), Options.Create(new ModuleConfigurationOption()));
            if (environmentFile)
            {
                var deploymentSource = inspector.GetSources().Single(source => source.PhysicalPath == path);
                deploymentSource.Kind.Should().Be(ConfigurationSourceKind.JsonFile);
                deploymentSource.IsWritable.Should().BeFalse();
                var file = await inspector.GetSourceFileViewAsync(deploymentSource.SourceKey, TestContext.Current.CancellationToken);
                file.Content.Should().Contain("original").And.NotContain("dictionary-secret").And.NotContain("list-secret");
                var originalFile = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                var writer = new ConfigurationJsonFileSourceWriter();
                var write = () => writer.WriteAsync(deploymentSource, "UnrelatedManaged:Enabled", ConfigurationMutationKind.Set,
                    ConfigurationStoredValue.FromJson("true"), null, TestContext.Current.CancellationToken);
                await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Deployment*read-only*");
                (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be(originalFile);
            }
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StartupSources:Name"] = "candidate",
                ["StartupSources:Credentials:main:Token"] = "changed-dictionary-secret"
            });

            var name = inspector.GetSourceChain(definition, LogicalPath.FromProperties("Name"));
            var credential = LogicalPath.FromProperties("Credentials").Append(new DictionaryKeySegment("main"))
                .Append(new PropertySegment("Token"));
            var item = LogicalPath.FromProperties("Items").Append(new ListIndexSegment(0)).Append(new PropertySegment("Token"));
            var masked = inspector.GetSourceChains(definition, [LogicalPath.Root, credential, item]);
            var disclosed = inspector.GetSourceChain(definition, credential, includeSensitive: true);

            name.Kind.Should().Be(ConfigurationKind.Startup);
            name.StartupActualValue!.DisplayValue.Should().Be("original");
            name.StartupActualValue.StartupCandidate!.DisplayValue.Should().Be("candidate");
            name.StartupActualValue.StartupCandidate.RequiresRestart.Should().BeTrue();
            var actualSource = name.StartupValues.Single(value => value.IsEffective).Source;
            actualSource.Kind.Should().Be(ConfigurationSourceKind.JsonFile);
            actualSource.PhysicalPath.Should().Be(path);
            actualSource.IsWritable.Should().BeFalse();
            actualSource.Optional.Should().BeFalse();
            foreach (var chain in masked)
            {
                chain.StartupActualValue!.IsSensitive.Should().BeTrue();
                chain.StartupActualValue.DisplayValue.Should().BeNull();
                chain.StartupActualValue.StartupCandidate!.DisplayValue.Should().BeNull();
                foreach (var value in chain.StartupValues.Concat(chain.Values))
                {
                    if (value.DisplayValue is null) continue;
                    value.DisplayValue.Should().NotContain("dictionary-secret");
                    value.DisplayValue.Should().NotContain("list-secret");
                }
            }
            disclosed.StartupActualValue!.DisplayValue.Should().Be("dictionary-secret");
            disclosed.StartupActualValue.StartupCandidate!.DisplayValue.Should().Be("changed-dictionary-secret");
            reader.Get<StartupSourceOptions>().Name.Should().Be("original");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Configuration("StartupSources", Kind = ConfigurationKind.Startup)]
    public sealed class StartupSourceOptions
    {
        public string Name { get; set; } = "default";
        public Dictionary<string, Credential> Credentials { get; set; } = [];
        public List<Credential> Items { get; set; } = [];
    }

    public sealed class Credential
    {
        [OptionSetting(IsSensitive = true)] public string Token { get; set; } = string.Empty;
    }
}
