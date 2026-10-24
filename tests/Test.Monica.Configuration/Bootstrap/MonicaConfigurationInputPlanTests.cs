using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Annotations;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.Models;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Configuration.Bootstrap;

public sealed class MonicaConfigurationInputPlanTests
{
    private const string DEFINITION_KEY = "test.configuration.input-plan";

    [Fact]
    public void LoadEffectiveOptionsSnapshot_WhenTypeIsStartup_ShouldRejectBeforeOpeningManagedReader()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var composition = new RecordingStoreComposition("Recording");
        var plan = MonicaConfigurationInputPlan.Create(inputs => inputs.UseConfigurationStore(composition));
        using var bootstrap = plan.BuildBootstrapConfiguration(directory.CreateHostBuilder());

        var load = () => plan.LoadEffectiveOptionsSnapshot(bootstrap, [typeof(StartupInputOptions)]);

        load.Should().Throw<InvalidOperationException>().WithMessage("*GetStartupOptions*managed input plan*");
        composition.CreatedReaders.Should().BeEmpty();
    }

    [Configuration("StartupInput", Kind = ConfigurationKind.Startup)]
    private sealed class StartupInputOptions
    {
        public int WorkerId { get; set; } = 1;
    }

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
    public async Task BuildBootstrapConfiguration_WhenSourcesOverlap_ShouldPreserveDeclarationOrderWithoutMutatingHost()
    {
        using var directory = new TemporaryConfigurationDirectory();
        await directory.WriteJsonAsync(
            "global.json",
            """
            {
              "Layered": {
                "Winner": "global",
                "GlobalOnly": "global"
              }
            }
            """);
        await directory.WriteJsonAsync(
            "app.json",
            """
            {
              "Layered": {
                "Winner": "app",
                "AppOnly": "app"
              }
            }
            """);
        var composition = new RecordingStoreComposition("Recording");
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition)
            .AddManagedJsonFile("global.json", optional: false, reloadOnChange: true)
            .AddManagedJsonFile("app.json", optional: false, reloadOnChange: true));
        var hostBuilder = directory.CreateHostBuilder();
        hostBuilder.Configuration["Layered:Winner"] = "host";
        hostBuilder.Configuration["Layered:HostOnly"] = "host";

        using var bootstrap = inputPlan.BuildBootstrapConfiguration(hostBuilder);

        bootstrap["Layered:Winner"].Should().Be("app");
        bootstrap["Layered:HostOnly"].Should().Be("host");
        bootstrap["Layered:GlobalOnly"].Should().Be("global");
        bootstrap["Layered:AppOnly"].Should().Be("app");
        hostBuilder.Configuration["Layered:Winner"].Should().Be("host");
        composition.CreatedReaders.Should().BeEmpty();
        var jsonProviders = bootstrap.Providers.OfType<JsonConfigurationProvider>().ToArray();
        jsonProviders.Select(static provider => provider.Source.Path)
            .Should().Equal("global.json", "app.json");
        jsonProviders.Should().AllSatisfy(static provider =>
            provider.Source.ReloadOnChange.Should().BeFalse());
    }

    [Fact]
    public async Task BuildBootstrapConfiguration_WhenPlansAreIndependent_ShouldNotLeakManagedSources()
    {
        using var directory = new TemporaryConfigurationDirectory();
        await directory.WriteJsonAsync("first.json", """{"Isolation":{"Value":"first"}}""");
        await directory.WriteJsonAsync("second.json", """{"Isolation":{"Value":"second"}}""");
        var firstPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(new RecordingStoreComposition("First"))
            .AddManagedJsonFile("first.json", optional: false, reloadOnChange: false));
        var secondPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(new RecordingStoreComposition("Second"))
            .AddManagedJsonFile("second.json", optional: false, reloadOnChange: false));
        var hostBuilder = directory.CreateHostBuilder();

        using var firstBootstrap = firstPlan.BuildBootstrapConfiguration(hostBuilder);
        using var secondBootstrap = secondPlan.BuildBootstrapConfiguration(hostBuilder);

        firstBootstrap["Isolation:Value"].Should().Be("first");
        secondBootstrap["Isolation:Value"].Should().Be("second");
        firstBootstrap.Providers.OfType<JsonConfigurationProvider>()
            .Should().ContainSingle(provider => provider.Source.Path == "first.json");
        secondBootstrap.Providers.OfType<JsonConfigurationProvider>()
            .Should().ContainSingle(provider => provider.Source.Path == "second.json");
    }

    [Fact]
    public async Task LoadEffectiveOptionsSnapshotAsync_WhenLayersOverlap_ShouldApplyPrecedenceAndDisposeReader()
    {
        using var directory = new TemporaryConfigurationDirectory();
        await directory.WriteJsonAsync(
            "global.json",
            """
            {
              "InputPlan": {
                "GlobalWinsStore": "global",
                "AppWinsGlobal": "global"
              }
            }
            """);
        await directory.WriteJsonAsync(
            "app.json",
            """
            {
              "InputPlan": {
                "AppWinsGlobal": "app"
              }
            }
            """);
        var composition = new RecordingStoreComposition(
            "Recording",
            """
            {
              "StoreWinsBootstrap": "store",
              "GlobalWinsStore": "store",
              "AppWinsGlobal": "store"
            }
            """);
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition)
            .AddManagedJsonFile("global.json", optional: false, reloadOnChange: false)
            .AddManagedJsonFile("app.json", optional: false, reloadOnChange: false));
        var hostBuilder = directory.CreateHostBuilder();
        hostBuilder.Configuration["InputPlan:StoreWinsBootstrap"] = "bootstrap";
        hostBuilder.Configuration["InputPlan:GlobalWinsStore"] = "bootstrap";
        hostBuilder.Configuration["InputPlan:AppWinsGlobal"] = "bootstrap";
        using var bootstrap = inputPlan.BuildBootstrapConfiguration(hostBuilder);

        var snapshot = await inputPlan.LoadEffectiveOptionsSnapshotAsync(
            bootstrap,
            [typeof(InputPlanOptions)],
            cancellationToken: TestContext.Current.CancellationToken);

        var options = snapshot.Get<InputPlanOptions>();
        options.StoreWinsBootstrap.Should().Be("store");
        options.GlobalWinsStore.Should().Be("global");
        options.AppWinsGlobal.Should().Be("app");
        composition.CreatedReaders.Should().ContainSingle();
        composition.CreatedReaders[0].BatchReadCount.Should().Be(1);
        composition.CreatedReaders[0].DisposeCount.Should().Be(0);
        composition.CreatedReaders[0].AsyncDisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task LoadEffectiveOptionsSnapshotAsync_WhenDocumentsAreMixed_ShouldReadOnceAndSeedOnlyMissingDefinitions()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var composition = new RecordingStoreComposition(
            "Recording",
            documents: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [DEFINITION_KEY] =
                    """
                    {
                      "StoreWinsBootstrap": "stored"
                    }
                    """
            });
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition));
        var hostBuilder = directory.CreateHostBuilder();
        hostBuilder.Configuration["InputPlan:StoreWinsBootstrap"] = "host-primary";
        hostBuilder.Configuration["InputPlanSecondary:Value"] = "host-secondary";
        using var bootstrap = inputPlan.BuildBootstrapConfiguration(hostBuilder);

        var snapshot = await inputPlan.LoadEffectiveOptionsSnapshotAsync(
            bootstrap,
            [typeof(InputPlanOptions), typeof(SecondaryInputPlanOptions), typeof(InputPlanOptions)],
            cancellationToken: TestContext.Current.CancellationToken);

        snapshot.Get<InputPlanOptions>().StoreWinsBootstrap.Should().Be("stored");
        snapshot.Get<SecondaryInputPlanOptions>().Value.Should().Be("host-secondary");
        composition.CreatedReaders.Should().ContainSingle();
        var reader = composition.CreatedReaders[0];
        reader.BatchReadCount.Should().Be(1);
        reader.LastRequestedKeys.Should().Equal(
            DEFINITION_KEY,
            "test.configuration.input-plan-secondary");
    }

    [Fact]
    public async Task LoadEffectiveOptionsSnapshotAsync_WhenFileDocumentIsMissing_ShouldNotCreateStoreRoot()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var storeRoot = Path.Combine(directory.RootPath, "configuration-store");
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseFileConfigurationStore(options => options.RootDirectory = storeRoot));
        var hostBuilder = directory.CreateHostBuilder();
        hostBuilder.Configuration["InputPlan:StoreWinsBootstrap"] = "transient-seed";
        using var bootstrap = inputPlan.BuildBootstrapConfiguration(hostBuilder);

        var snapshot = await inputPlan.LoadEffectiveOptionsSnapshotAsync(
            bootstrap,
            [typeof(InputPlanOptions)],
            cancellationToken: TestContext.Current.CancellationToken);

        snapshot.Get<InputPlanOptions>().StoreWinsBootstrap.Should().Be("transient-seed");
        Directory.Exists(storeRoot).Should().BeFalse();
    }

    [Fact]
    public void LoadEffectiveOptionsSnapshot_WhenPlanUsesClrFullName_ShouldBindConventionAndDisposeReaderSynchronously()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var composition = new RecordingStoreComposition("Recording");
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition)
            .UseSectionPathConvention(ConfigurationSectionPathConvention.ClrFullName));
        var hostBuilder = directory.CreateHostBuilder();
        var sectionPath = typeof(ConventionOptions).FullName!.Replace('.', ':');
        hostBuilder.Configuration[$"{sectionPath}:Value"] = "full-name";
        using var bootstrap = inputPlan.BuildBootstrapConfiguration(hostBuilder);

        var snapshot = inputPlan.LoadEffectiveOptionsSnapshot(
            bootstrap,
            [typeof(ConventionOptions)]);

        snapshot.Get<ConventionOptions>().Value.Should().Be("full-name");
        composition.CreatedReaders.Should().ContainSingle();
        composition.CreatedReaders[0].DisposeCount.Should().Be(1);
        composition.CreatedReaders[0].AsyncDisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task LoadEffectiveOptionsSnapshotAsync_WhenReaderFails_ShouldDisposeReader()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var composition = new RecordingStoreComposition(
            "Failing",
            failure: new InvalidOperationException("Store read failed."));
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition));
        var hostBuilder = directory.CreateHostBuilder();
        using var bootstrap = inputPlan.BuildBootstrapConfiguration(hostBuilder);

        Func<Task> load = () => inputPlan.LoadEffectiveOptionsSnapshotAsync(
            bootstrap,
            [typeof(InputPlanOptions)],
            cancellationToken: TestContext.Current.CancellationToken);

        await load.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Store read failed.");
        composition.CreatedReaders.Should().ContainSingle();
        composition.CreatedReaders[0].AsyncDisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task LoadEffectiveOptionsSnapshotAsync_WhenReaderReturnsWrongCount_ShouldRejectResult()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var composition = new RecordingStoreComposition(
            "Wrong count",
            resultsFactory: static _ => []);
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition));
        using var bootstrap = inputPlan.BuildBootstrapConfiguration(directory.CreateHostBuilder());

        Func<Task> load = () => inputPlan.LoadEffectiveOptionsSnapshotAsync(
            bootstrap,
            [typeof(InputPlanOptions)],
            cancellationToken: TestContext.Current.CancellationToken);

        await load.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 0 documents for 1 requested definitions*");
    }

    [Fact]
    public async Task LoadEffectiveOptionsSnapshotAsync_WhenReaderReturnsWrongDefinition_ShouldRejectResult()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var composition = new RecordingStoreComposition(
            "Wrong definition",
            resultsFactory: static _ =>
            [
                new ConfigurationEffectiveValueDocument
                {
                    DefinitionKey = "test.configuration.wrong",
                    Json = "{}",
                    Version = 1,
                    SchemaVersion = 1,
                    LastModifiedTime = DateTimeOffset.UnixEpoch
                }
            ]);
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition));
        using var bootstrap = inputPlan.BuildBootstrapConfiguration(directory.CreateHostBuilder());

        Func<Task> load = () => inputPlan.LoadEffectiveOptionsSnapshotAsync(
            bootstrap,
            [typeof(InputPlanOptions)],
            cancellationToken: TestContext.Current.CancellationToken);

        await load.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*test.configuration.wrong*test.configuration.input-plan*");
    }

    [Fact]
    public async Task LoadEffectiveOptionsSnapshotAsync_WhenStoredJsonIsMalformed_ShouldFailProjection()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var composition = new RecordingStoreComposition("Malformed", "{");
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition));
        using var bootstrap = inputPlan.BuildBootstrapConfiguration(directory.CreateHostBuilder());

        Func<Task> load = () => inputPlan.LoadEffectiveOptionsSnapshotAsync(
            bootstrap,
            [typeof(InputPlanOptions)],
            cancellationToken: TestContext.Current.CancellationToken);

        await load.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to project*test.configuration.input-plan*");
    }

    [Fact]
    public async Task LoadEffectiveOptionsSnapshotAsync_WhenStoredValueCannotBind_ShouldFailBinding()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var composition = new RecordingStoreComposition(
            "Invalid binding",
            """
            {
              "Count": "not-an-integer"
            }
            """);
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition));
        using var bootstrap = inputPlan.BuildBootstrapConfiguration(directory.CreateHostBuilder());

        Func<Task> load = () => inputPlan.LoadEffectiveOptionsSnapshotAsync(
            bootstrap,
            [typeof(BindingFailureOptions)],
            cancellationToken: TestContext.Current.CancellationToken);

        await load.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to bind*BindingFailureOptions*BindingFailure*");
    }

    [Fact]
    public async Task LoadEffectiveOptionsSnapshotAsync_WhenCancelled_ShouldPropagateCancellationAndDisposeReader()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var composition = new RecordingStoreComposition("Cancelled");
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition));
        using var bootstrap = inputPlan.BuildBootstrapConfiguration(directory.CreateHostBuilder());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> load = () => inputPlan.LoadEffectiveOptionsSnapshotAsync(
            bootstrap,
            [typeof(InputPlanOptions)],
            cancellationToken: cancellation.Token);

        await load.Should().ThrowAsync<OperationCanceledException>();
        composition.CreatedReaders.Should().ContainSingle();
        composition.CreatedReaders[0].AsyncDisposeCount.Should().Be(1);
    }

    [Fact]
    public void LoadEffectiveOptionsSnapshot_WhenOptionsTypesAreEmpty_ShouldRejectBeforeCreatingReader()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var composition = new RecordingStoreComposition("Recording");
        var inputPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(composition));
        var hostBuilder = directory.CreateHostBuilder();
        using var bootstrap = inputPlan.BuildBootstrapConfiguration(hostBuilder);

        Action load = () => inputPlan.LoadEffectiveOptionsSnapshot(
            bootstrap,
            Array.Empty<Type>());

        load.Should().Throw<ArgumentException>()
            .WithParameterName("optionsTypes");
        composition.CreatedReaders.Should().BeEmpty();
    }

    [Fact]
    public void LoadEffectiveOptionsSnapshot_WhenBootstrapBelongsToAnotherPlan_ShouldRejectBeforeCreatingReader()
    {
        using var directory = new TemporaryConfigurationDirectory();
        var firstPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(new RecordingStoreComposition("First")));
        var secondComposition = new RecordingStoreComposition("Second");
        var secondPlan = MonicaConfigurationInputPlan.Create(inputs => inputs
            .UseConfigurationStore(secondComposition));
        using var bootstrap = firstPlan.BuildBootstrapConfiguration(directory.CreateHostBuilder());

        Action load = () => secondPlan.LoadEffectiveOptionsSnapshot(
            bootstrap,
            [typeof(InputPlanOptions)]);

        load.Should().Throw<InvalidOperationException>()
            .WithMessage("*different*input plan*");
        secondComposition.CreatedReaders.Should().BeEmpty();
    }

    [Configuration("InputPlan", DefinitionKey = DEFINITION_KEY)]
    private sealed class InputPlanOptions
    {
        public string StoreWinsBootstrap { get; set; } = string.Empty;

        public string GlobalWinsStore { get; set; } = string.Empty;

        public string AppWinsGlobal { get; set; } = string.Empty;
    }

    [Configuration(DefinitionKey = "test.configuration.input-plan-convention")]
    private sealed class ConventionOptions
    {
        public string Value { get; set; } = string.Empty;
    }

    [Configuration("InputPlanSecondary", DefinitionKey = "test.configuration.input-plan-secondary")]
    private sealed class SecondaryInputPlanOptions
    {
        public string Value { get; set; } = "default-secondary";
    }

    [Configuration("BindingFailure", DefinitionKey = "test.configuration.input-plan-binding-failure")]
    private sealed class BindingFailureOptions
    {
        public int Count { get; set; }
    }

    private sealed class RecordingStoreComposition(
        string name,
        string? effectiveJson = null,
        Exception? failure = null,
        IReadOnlyDictionary<string, string>? documents = null,
        Func<IReadOnlyList<string>, IReadOnlyList<ConfigurationEffectiveValueDocument?>>? resultsFactory = null)
        : IMonicaConfigurationStoreComposition
    {
        public string Name { get; } = name;

        public List<RecordingEffectiveValueReader> CreatedReaders { get; } = [];

        public void ConfigureRuntime(
            ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> module)
        {
        }

        public IConfigurationEffectiveValueReader CreateStartupReader()
        {
            var reader = new RecordingEffectiveValueReader(
                effectiveJson,
                failure,
                documents,
                resultsFactory);
            CreatedReaders.Add(reader);
            return reader;
        }
    }

    private sealed class RecordingEffectiveValueReader(
        string? effectiveJson,
        Exception? failure,
        IReadOnlyDictionary<string, string>? documents,
        Func<IReadOnlyList<string>, IReadOnlyList<ConfigurationEffectiveValueDocument?>>? resultsFactory)
        : IConfigurationEffectiveValueReader, IDisposable, IAsyncDisposable
    {
        public ConfigurationStoreDescriptor Descriptor { get; } = new()
        {
            StoreKey = "test:recording",
            DisplayName = "Recording",
            Kind = ConfigurationStoreKind.File,
            SupportsEffectiveValues = true
        };

        public int BatchReadCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int AsyncDisposeCount { get; private set; }

        public IReadOnlyList<string> LastRequestedKeys { get; private set; } = [];

        public Task<ConfigurationEffectiveValueDocument?> GetAsync(
            string definitionKey,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<ConfigurationEffectiveValueDocument?>(null);
        }

        public Task<IReadOnlyList<ConfigurationEffectiveValueDocument?>> GetManyAsync(
            IReadOnlyList<string> definitionKeys,
            CancellationToken cancellationToken)
        {
            BatchReadCount++;
            LastRequestedKeys = definitionKeys.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null)
            {
                return Task.FromException<IReadOnlyList<ConfigurationEffectiveValueDocument?>>(failure);
            }

            if (resultsFactory is not null)
            {
                return Task.FromResult(resultsFactory(definitionKeys));
            }

            IReadOnlyList<ConfigurationEffectiveValueDocument?> results = definitionKeys
                .Select(definitionKey => TryGetJson(definitionKey, out var json)
                    ? CreateDocument(definitionKey, json)
                    : null)
                .ToArray();
            return Task.FromResult(results);
        }

        private bool TryGetJson(string definitionKey, out string json)
        {
            if (documents is not null && documents.TryGetValue(definitionKey, out var storedJson))
            {
                json = storedJson;
                return true;
            }

            json = effectiveJson ?? string.Empty;
            return effectiveJson is not null;
        }

        public void Dispose()
        {
            DisposeCount++;
        }

        public ValueTask DisposeAsync()
        {
            AsyncDisposeCount++;
            return ValueTask.CompletedTask;
        }

        private static ConfigurationEffectiveValueDocument CreateDocument(
            string definitionKey,
            string json)
        {
            return new ConfigurationEffectiveValueDocument
            {
                DefinitionKey = definitionKey,
                Json = json,
                Version = 1,
                SchemaVersion = 1,
                LastModifiedTime = DateTimeOffset.UnixEpoch
            };
        }
    }

    private sealed class TemporaryConfigurationDirectory : IDisposable
    {
        public TemporaryConfigurationDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"monica-configuration-input-plan-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public HostApplicationBuilder CreateHostBuilder()
        {
            return new HostApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = RootPath,
                DisableDefaults = true
            });
        }

        public Task WriteJsonAsync(string relativePath, string json)
        {
            return File.WriteAllTextAsync(
                Path.Combine(RootPath, relativePath),
                json,
                TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
