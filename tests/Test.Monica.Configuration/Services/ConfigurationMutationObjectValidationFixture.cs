using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Annotations;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Configuration.Stores.File;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services;

/// <summary>A locally discovered value contract used by isolated, fully started mutation hosts.</summary>
public class MutationRangeValues : IValidatableObject
{
    public int Minimum { get; set; } = 10;
    public int Maximum { get; set; } = 20;
    public bool StageA { get; set; }
    public bool StageB { get; set; }
    public bool StageC { get; set; }
    [ConfigurationKeyName("note-text")]
    public string? Note { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Minimum > Maximum)
            yield return new ValidationResult("Minimum must not exceed Maximum.", [nameof(Minimum), nameof(Maximum)]);
        if (StageA && StageB && !StageC)
            yield return new ValidationResult("Stages A and B require stage C.", [nameof(StageA), nameof(StageB), nameof(StageC)]);
        if (Note == "blocked")
            yield return new ValidationResult("The note is blocked.", [nameof(Note)]);
    }
}

internal sealed class ConfigurationMutationObjectValidationFixture : IAsyncDisposable
{
    internal const string DEFINITION_KEY = "test.object-mutation.range";
    internal const string SECTION_PATH = "ObjectMutation";
    private readonly string _directory;
    private readonly MonicaTestApplication _application;

    private ConfigurationMutationObjectValidationFixture(string directory, MonicaTestApplication application)
    {
        _directory = directory;
        _application = application;
        Definition = Services.GetRequiredService<IConfigurationDefinitionRegistry>().GetRequired(DEFINITION_KEY);
        Values = Services.GetRequiredService<RecordingEffectiveValueStore>();
        Reloads = Services.GetRequiredService<RecordingReloadCoordinator>();
        ResetObservations();
    }

    internal IServiceProvider Services => _application.Services;
    internal ConfigurationDefinition Definition { get; }
    internal RecordingEffectiveValueStore Values { get; }
    internal RecordingReloadCoordinator Reloads { get; }
    internal RecordingJsonFileSourceWriter Writer => Services.GetRequiredService<RecordingJsonFileSourceWriter>();
    internal IConfigurationMutationGroupApplyService Mutations => Services.GetRequiredService<IConfigurationMutationGroupApplyService>();
    internal IConfigurationHistoryStore History => Services.GetRequiredService<IConfigurationHistoryStore>();
    internal IConfigurationChangeNotifier Notifier => Services.GetRequiredService<IConfigurationChangeNotifier>();
    internal IConfiguration Configuration => Services.GetRequiredService<IConfiguration>();

    internal static async Task<ConfigurationMutationObjectValidationFixture> CreateAsync(params string[] sourceJson)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"monica-object-mutation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        try
        {
            for (var index = 0; index < sourceJson.Length; index++)
            {
                var path = Path.Combine(directory, $"source-{index}.json");
                await File.WriteAllTextAsync(path, sourceJson[index], TestContext.Current.CancellationToken);
                paths.Add(path);
            }
            var application = await new MutationApplicationFactory(directory, paths)
                .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
            return new ConfigurationMutationObjectValidationFixture(directory, application);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    internal ConfigurationMutationCommand Set(string requestId, string property, int value,
        ConfigurationMutationTarget? target = null) => new()
    {
        RequestId = requestId, DefinitionKey = Definition.DefinitionKey,
        LogicalPath = LogicalPath.FromProperties(property), Value = ConfigurationStoredValue.FromJson(value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        MutationKind = ConfigurationMutationKind.Set, ExpectedSchemaVersion = Definition.SchemaVersion,
        ExpectedSchemaHash = Definition.SchemaHash, Target = target ?? new ConfigurationEffectiveStoreMutationTarget()
    };

    internal ConfigurationExternalSourceMutationTarget Source(int index)
    {
        var path = Path.Combine(_directory, $"source-{index}.json");
        var source = Services.GetRequiredService<IConfigurationSourceInspector>().GetSources()
            .Single(candidate => string.Equals(candidate.PhysicalPath, path, StringComparison.OrdinalIgnoreCase));
        return new ConfigurationExternalSourceMutationTarget { SourceKey = source.SourceKey };
    }

    internal async Task SetStoredBaselineAsync(string json)
    {
        await ChangeStoredDocumentWithoutReloadAsync(json);
        await Reloads.ReloadMonicaProjectionAsync(DEFINITION_KEY, null, TestContext.Current.CancellationToken);
        ResetObservations();
    }

    internal async Task ChangeStoredDocumentWithoutReloadAsync(string json)
    {
        var current = await Values.GetAsync(DEFINITION_KEY, TestContext.Current.CancellationToken);
        var complete = JsonNode.Parse(current!.Json)!.AsObject();
        foreach (var property in JsonNode.Parse(json)!.AsObject()) complete[property.Key] = property.Value?.DeepClone();
        await Values.SaveAsync(new ConfigurationEffectiveValueSaveRequest
        {
            Definition = Definition, Json = complete.ToJsonString(), ExpectedVersion = current.Version
        }, TestContext.Current.CancellationToken);
        ResetObservations();
    }

    internal void ChangeLoadedStoreContribution(string property, string value)
    {
        var source = Services.GetRequiredService<IConfigurationSourceInspector>().GetSources()
            .Single(candidate => candidate.Kind == ConfigurationSourceKind.MonicaEffectiveStore);
        ((IConfigurationRoot)Configuration).Providers.ElementAt(source.PriorityIndex).Set($"{SECTION_PATH}:{property}", value);
    }

    internal Task<string> ReadSourceAsync(int index) => File.ReadAllTextAsync(
        Path.Combine(_directory, $"source-{index}.json"), TestContext.Current.CancellationToken);

    internal Task ChangePhysicalSourceWithoutReloadAsync(int index, string json) => File.WriteAllTextAsync(
        Path.Combine(_directory, $"source-{index}.json"), json, TestContext.Current.CancellationToken);

    internal void AdoptWrittenSource(ConfigurationSourceDescriptor source)
    {
        // A file watcher can adopt one provider independently while other files still have their old values.
        // Loading that actual provider avoids reentering the mutation service's runtime snapshot lock.
        ((IConfigurationRoot)Configuration).Providers.ElementAt(source.PriorityIndex).Load();
    }

    internal void ResetObservations()
    {
        Values.SaveCount = 0;
        Reloads.ReloadCount = 0;
        Writer.WriteCount = 0;
        Notifier.ClearReceivedCalls();
    }

    public async ValueTask DisposeAsync()
    {
        await _application.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class MutationApplicationFactory(string directory, IReadOnlyList<string> paths)
        : MonicaTestApplicationFactory<MutationRangeValues>
    {
        private readonly Assembly _assembly = CreateDiscoveryAssembly();

        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => [_assembly];

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{SECTION_PATH}:Minimum"] = "10", [$"{SECTION_PATH}:Maximum"] = "20"
            });
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            var inputPlan = MonicaConfigurationInputPlan.Create(inputs =>
            {
                inputs.UseFileConfigurationStore(options => options.RootDirectory = Path.Combine(directory, "store"));
                foreach (var path in paths) inputs.AddManagedJsonFile(path, optional: false, reloadOnChange: false);
            });
            builder.AddConfiguration(inputPlan, options => options.RuntimeValidationBehavior = ConfigurationRuntimeValidationBehavior.FailFast);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton<RecordingEffectiveValueStore>();
            services.Replace(ServiceDescriptor.Singleton<IConfigurationEffectiveValueStore>(provider =>
                provider.GetRequiredService<RecordingEffectiveValueStore>()));
            services.AddSingleton<ConfigurationProviderReloadCoordinator>();
            services.AddSingleton<RecordingReloadCoordinator>();
            services.Replace(ServiceDescriptor.Singleton<IConfigurationReloadCoordinator>(provider =>
                provider.GetRequiredService<RecordingReloadCoordinator>()));
            services.AddSingleton(Substitute.For<IConfigurationChangeNotifier>());
            services.AddSingleton<ConfigurationJsonFileSourceWriter>();
            services.AddSingleton<RecordingJsonFileSourceWriter>();
            services.Replace(ServiceDescriptor.Singleton<IConfigurationJsonFileSourceWriter>(provider =>
                provider.GetRequiredService<RecordingJsonFileSourceWriter>()));
        }

        private static Assembly CreateDiscoveryAssembly()
        {
            // Each host discovers only its own executable owner, never unrelated options in this test assembly.
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName($"Test.Monica.Configuration.Mutation.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("Main").DefineType("MutationRangeOptions",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, typeof(MutationRangeValues));
            type.DefineDefaultConstructor(MethodAttributes.Public);
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(ConfigurationAttribute).GetConstructor([typeof(string)])!, [SECTION_PATH],
                [typeof(ConfigurationAttribute).GetProperty(nameof(ConfigurationAttribute.DefinitionKey))!], [DEFINITION_KEY]));
            type.CreateType();
            return assembly;
        }
    }

    internal sealed class RecordingEffectiveValueStore(FileConfigurationStore inner) : IConfigurationEffectiveValueStore
    {
        internal int SaveCount { get; set; }
        public ConfigurationStoreDescriptor Descriptor => inner.Descriptor;
        public Task<ConfigurationEffectiveValueDocument?> GetAsync(string definitionKey, CancellationToken cancellationToken)
            => inner.GetAsync(definitionKey, cancellationToken);
        public Task<IReadOnlyList<ConfigurationEffectiveValueDocument?>> GetManyAsync(IReadOnlyList<string> definitionKeys, CancellationToken cancellationToken)
            => inner.GetManyAsync(definitionKeys, cancellationToken);
        public Task<ConfigurationEffectiveValueDocument> EnsureCreatedAsync(ConfigurationDefinition definition, string seedJson, CancellationToken cancellationToken)
            => inner.EnsureCreatedAsync(definition, seedJson, cancellationToken);
        public Task<IReadOnlyList<ConfigurationEffectiveValueDocument>> EnsureCreatedAsync(IReadOnlyList<ConfigurationEffectiveValueSeed> seeds, CancellationToken cancellationToken)
            => inner.EnsureCreatedAsync(seeds, cancellationToken);
        public Task<ConfigurationEffectiveValueDocument> SaveAsync(ConfigurationEffectiveValueSaveRequest request, CancellationToken cancellationToken)
        {
            SaveCount++;
            return inner.SaveAsync(request, cancellationToken);
        }
    }

    internal sealed class RecordingReloadCoordinator(ConfigurationProviderReloadCoordinator inner) : IConfigurationReloadCoordinator
    {
        internal int ReloadCount { get; set; }
        internal Exception? FullProjectionReloadFailure { get; set; }
        public Task ReloadMonicaProjectionAsync(CancellationToken cancellationToken)
        {
            ReloadCount++;
            return FullProjectionReloadFailure is { } failure
                ? Task.FromException(failure)
                : inner.ReloadMonicaProjectionAsync(cancellationToken);
        }
        public Task ReloadMonicaProjectionAsync(string definitionKey, long? minimumVersion, CancellationToken cancellationToken)
        {
            ReloadCount++;
            return inner.ReloadMonicaProjectionAsync(definitionKey, minimumVersion, cancellationToken);
        }
        public long? GetLoadedMonicaProjectionVersion(string definitionKey) => inner.GetLoadedMonicaProjectionVersion(definitionKey);
        public Task ReloadRuntimeConfigurationAsync(CancellationToken cancellationToken)
        {
            ReloadCount++;
            return inner.ReloadRuntimeConfigurationAsync(cancellationToken);
        }
    }

    internal sealed class RecordingJsonFileSourceWriter(ConfigurationJsonFileSourceWriter inner) : IConfigurationJsonFileSourceWriter
    {
        internal int WriteCount { get; set; }
        internal Action<ConfigurationSourceDescriptor>? AfterWrite { get; set; }

        public Task<ConfigurationJsonFilePhysicalValuesSnapshot> ReadPhysicalValuesAsync(ConfigurationSourceDescriptor source,
            IReadOnlyList<string> configurationPaths, CancellationToken cancellationToken)
            => inner.ReadPhysicalValuesAsync(source, configurationPaths, cancellationToken);
        public Task<ConfigurationJsonFileValuesSnapshot> ReadValuesAsync(ConfigurationSourceDescriptor source, ConfigurationDefinition definition,
            IReadOnlyList<string> configurationPaths, CancellationToken cancellationToken)
            => inner.ReadValuesAsync(source, definition, configurationPaths, cancellationToken);
        public Task<string?> GetRevisionAsync(ConfigurationSourceDescriptor source, CancellationToken cancellationToken)
            => inner.GetRevisionAsync(source, cancellationToken);
        public async Task<ConfigurationJsonFileWriteResult> WriteAsync(ConfigurationSourceDescriptor source, string configurationPath,
            ConfigurationMutationKind mutationKind, ConfigurationStoredValue value, string? expectedRevision, CancellationToken cancellationToken)
        {
            var result = await inner.WriteAsync(source, configurationPath, mutationKind, value, expectedRevision, cancellationToken);
            WriteCount++;
            AfterWrite?.Invoke(source);
            return result;
        }
        public async Task<ConfigurationJsonFileBatchWriteResult> WriteBatchAsync(ConfigurationSourceDescriptor source,
            IReadOnlyList<ConfigurationJsonFileMutation> mutations, string? expectedRevision, CancellationToken cancellationToken)
        {
            var result = await inner.WriteBatchAsync(source, mutations, expectedRevision, cancellationToken);
            WriteCount++;
            AfterWrite?.Invoke(source);
            return result;
        }
    }
}
