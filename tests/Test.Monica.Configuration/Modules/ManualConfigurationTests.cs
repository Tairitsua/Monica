using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Reflection.Emit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Annotations;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Configuration.Modules;

public sealed class ManualConfigurationTests : IDisposable
{
    private const string KEY = "test.manual-values";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"monica-manual-{Guid.NewGuid():N}");
    private readonly Assembly _assembly = CreateDiscoveryAssembly();

    [Fact]
    public async Task MissingDocument_ShouldProjectValidatedTemporaryValuesWithoutCreatingAnyStoreFiles()
    {
        var factory = new ManualApplicationFactory(_directory, _assembly);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var facade = app.Services.GetRequiredService<ConfigurationFacade>();

        var state = (await facade.GetDefinitionStateAsync(KEY)).Data!;
        state.Should().NotBeNull();
        state.IsUnpersisted.Should().BeTrue();
        state.EffectiveValueVersion.Should().BeNull();
        state.EffectiveValues.Should().OnlyContain(value => value.IsUnpersisted && value.Version == null);
        var secret = state.EffectiveValues.Single(value => value.LogicalPath == PathFor(nameof(ManualValues.Secret)));
        secret.IsSensitive.Should().BeTrue();
        secret.DisplayValue.Should().BeNull();
        var chain = (await facade.GetSourceChainAsync(KEY, PathFor(nameof(ManualValues.Maximum)))).Data!;
        chain.Values.Single(value => value.IsEffective).IsUnpersisted.Should().BeTrue();
        chain.Values.Single(value => value.IsEffective).Source.IsWritable.Should().BeFalse();
        chain.Values.Single(value => value.IsEffective).Source.ReadOnlyReason.Should().Contain("active published definition");
        var summary = (await facade.GetDefinitionsAsync()).Data!.Single();
        summary.CanMutate.Should().BeFalse();
        summary.MutationReadOnlyReason.Should().Contain("active published definition");
        (await app.Services.GetRequiredService<IConfigurationEffectiveValueStore>()
            .GetAsync(KEY, TestContext.Current.CancellationToken)).Should().BeNull();
        Directory.Exists(_directory).Should().BeFalse();
    }

    [Fact]
    public async Task MissingDocument_WhenObjectRuleFails_ShouldRejectEvenWithDiagnosticOnlyPolicy()
    {
        var factory = new ManualApplicationFactory(_directory, _assembly, maximum: 5);
        var start = () => factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        await start.Should().ThrowAsync<ConfigurationRuntimeValidationException>();
        Directory.Exists(_directory).Should().BeFalse();
    }

    [Fact]
    public async Task Reload_WhenDocumentRemainsMissing_ShouldUseCurrentInputsInsteadOfItsOwnOldProjection()
    {
        var factory = new ManualApplicationFactory(_directory, _assembly);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var reload = app.Services.GetRequiredService<IConfigurationReloadCoordinator>();
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        var inputs = ((IConfigurationRoot)configuration).Providers.OfType<MemoryConfigurationProvider>()
            .Last(provider => provider.TryGet("ManualValues:Maximum", out _));
        configuration["ManualValues:Maximum"].Should().Be("20");

        inputs.Set("ManualValues:Maximum", "30");
        await reload.ReloadMonicaProjectionAsync(KEY, null, TestContext.Current.CancellationToken);
        configuration["ManualValues:Maximum"].Should().Be("30");
        inputs.Set("ManualValues:Maximum", "40");
        await reload.ReloadMonicaProjectionAsync(TestContext.Current.CancellationToken);
        configuration["ManualValues:Maximum"].Should().Be("40");
        Directory.Exists(_directory).Should().BeFalse();

        var definition = app.Services.GetRequiredService<IConfigurationDefinitionRegistry>().GetRequired(KEY);
        await app.Services.GetRequiredService<IConfigurationEffectiveValueStore>().EnsureCreatedAsync(definition,
            """{"Minimum":10,"Maximum":50,"Secret":"stored-secret"}""", TestContext.Current.CancellationToken);
        await reload.ReloadMonicaProjectionAsync(KEY, 1, TestContext.Current.CancellationToken);
        var state = (await app.Services.GetRequiredService<ConfigurationFacade>().GetDefinitionStateAsync(KEY)).Data!;
        state.IsUnpersisted.Should().BeFalse();
        state.EffectiveValueVersion.Should().Be(1);
        configuration["ManualValues:Maximum"].Should().Be("50");
    }

    [Fact]
    public async Task ExistingPublication_ShouldAllowValidatedEditsButRejectPublicationDriftAfterPreview()
    {
        await using (var deployed = await new ManualApplicationFactory(_directory, _assembly, autonomous: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken)) { }
        await using var app = await new ManualApplicationFactory(_directory, _assembly)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var facade = app.Services.GetRequiredService<ConfigurationFacade>();
        var definition = app.Services.GetRequiredService<IConfigurationDefinitionRegistry>().GetRequired(KEY);
        (await facade.GetDefinitionsAsync()).Data!.Single().CanMutate.Should().BeTrue();
        var service = app.Services.GetRequiredService<IConfigurationMutationGroupApplyService>();
        var request = Mutation(definition, 30);
        var unreviewed = await service.ApplyAsync(request, TestContext.Current.CancellationToken);
        unreviewed.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Rejected);
        var preview = await service.PreviewAsync(request, TestContext.Current.CancellationToken);
        preview.CanApply.Should().BeTrue();
        var applied = await service.ApplyAsync(request with { ExpectedValidationFingerprint = preview.ValidationFingerprint },
            TestContext.Current.CancellationToken);
        applied.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Applied);

        var invalid = await service.PreviewAsync(Mutation(definition, 5, 2), TestContext.Current.CancellationToken);
        invalid.CanApply.Should().BeFalse();
        var pending = Mutation(definition, 40, 2);
        var reviewed = await service.PreviewAsync(pending, TestContext.Current.CancellationToken);
        reviewed.CanApply.Should().BeTrue();
        await app.Services.GetRequiredService<IConfigurationMetadataStore>().PublishAsync(
            ConfigurationDefinitionPublicationBatch.Create(new ConfigurationPublisherIdentity
            {
                PublisherKey = "changed-owner", InstanceId = "changed-owner:1", Name = "Changed owner"
            }, [definition with { ValidationContract = definition.ValidationContract with { Revision = "different-code" } }]),
            TestContext.Current.CancellationToken);

        var rejected = () => service.ApplyAsync(pending with { ExpectedValidationFingerprint = reviewed.ValidationFingerprint },
            TestContext.Current.CancellationToken);
        await rejected.Should().ThrowAsync<ConfigurationValidationFailedException>().WithMessage("*active publication*");
        (await app.Services.GetRequiredService<IConfigurationEffectiveValueStore>()
            .GetAsync(KEY, TestContext.Current.CancellationToken))!.Version.Should().Be(2);
    }

    [Fact]
    public async Task ExternalJson_ShouldRemainReadOnlyInManualMode()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "inputs.json");
        const string json = """{"ManualValues":{"Maximum":25}}""";
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
        await using var app = await new ManualApplicationFactory(Path.Combine(_directory, "store"), _assembly, jsonPath: path)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var facade = app.Services.GetRequiredService<ConfigurationFacade>();
        var source = (await facade.GetConfigurationSourcesAsync()).Data!.Single(value => value.PhysicalPath == path);
        source.IsWritable.Should().BeFalse();
        source.ReadOnlyReason.Should().Contain("Manual");
        var definition = app.Services.GetRequiredService<IConfigurationDefinitionRegistry>().GetRequired(KEY);
        var request = Mutation(definition, 30);
        request = request with { Commands = [request.Commands[0] with
        {
            Target = new ConfigurationExternalSourceMutationTarget { SourceKey = source.SourceKey }
        }] };
        var preview = () => app.Services.GetRequiredService<IConfigurationMutationGroupApplyService>()
            .PreviewAsync(request, TestContext.Current.CancellationToken);
        await preview.Should().ThrowAsync<ConfigurationValidationFailedException>().WithMessage("*read-only in Manual mode*");
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be(json);
        Directory.Exists(Path.Combine(_directory, "store")).Should().BeFalse();
    }

    [Fact]
    public async Task ExistingDocumentWithOlderSchema_ShouldRemainReadOnlyWithoutLocalMigration()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new ManualApplicationFactory(_directory, _assembly).CreateAsync(cancellationToken: token);
        var definition = app.Services.GetRequiredService<IConfigurationDefinitionRegistry>().GetRequired(KEY);
        await app.Services.GetRequiredService<IConfigurationMetadataStore>().PublishAsync(
            ConfigurationDefinitionPublicationBatch.Create(new ConfigurationPublisherIdentity
            {
                PublisherKey = "deployed-owner", InstanceId = "deployed-owner:1", Name = "Deployed owner"
            }, [definition with { SchemaVersion = 2 }]), token);
        var values = app.Services.GetRequiredService<IConfigurationEffectiveValueStore>();
        await values.EnsureCreatedAsync(definition, """{"Minimum":10,"Maximum":20,"Secret":"stored"}""", token);
        var facade = app.Services.GetRequiredService<ConfigurationFacade>();

        var summary = (await facade.GetDefinitionsAsync()).Data!.Single();
        summary.CanMutate.Should().BeFalse();
        summary.MutationReadOnlyReason.Should().Contain("stored document schema version");
        (await values.GetAsync(KEY, token))!.SchemaVersion.Should().Be(1);
        await app.Services.GetRequiredService<IConfigurationMetadataStore>().RetirePublisherAsync(new ConfigurationPublisherIdentity
        {
            PublisherKey = "deployed-owner", InstanceId = "deployed-owner:1", Name = "Deployed owner"
        }, token);
        (await facade.GetDefinitionPublicationOverviewAsync(KEY)).Data!.LifecycleState.Should().Be(ConfigurationDefinitionLifecycleState.Retired);
        (await facade.PreviewDefinitionPurgeAsync(KEY)).Data!.ReadOnlyReason.Should().Contain("Manual");
        var purge = await facade.PurgeDefinitionAsync(new ConfigurationDefinitionPurgeRequest { DefinitionKey = KEY, ExpectedDefinitionRevision = 1 });
        purge.Message.Should().Contain("read-only in Manual mode");
        (await facade.DeleteUnifiedVersionAsync(1)).Message.Should().Contain("read-only in Manual mode");
    }

    private static ConfigurationMutationGroupApplyRequest Mutation(ConfigurationDefinition definition, int maximum, long version = 1)
        => new()
        {
            Label = "Manual edit", Commands = [new ConfigurationMutationCommand
            {
                RequestId = "maximum", DefinitionKey = KEY, LogicalPath = PathFor(nameof(ManualValues.Maximum)),
                MutationKind = ConfigurationMutationKind.Set,
                Value = ConfigurationStoredValue.FromJson(maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ExpectedSchemaVersion = definition.SchemaVersion, ExpectedSchemaHash = definition.SchemaHash,
                Target = new ConfigurationEffectiveStoreMutationTarget { ExpectedVersion = version }
            }]
        };

    private static LogicalPath PathFor(string name) => LogicalPath.Root.Append(new PropertySegment(name));

    private static Assembly CreateDiscoveryAssembly()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"Test.ManualConfiguration.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Main").DefineType("ManualOptions",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, typeof(ManualValues));
        type.DefineDefaultConstructor(MethodAttributes.Public);
        type.SetCustomAttribute(new CustomAttributeBuilder(typeof(ConfigurationAttribute).GetConstructor([typeof(string)])!,
            ["ManualValues"], [typeof(ConfigurationAttribute).GetProperty(nameof(ConfigurationAttribute.DefinitionKey))!], [KEY]));
        type.CreateType();
        return assembly;
    }

    private sealed class ManualApplicationFactory(string directory, Assembly assembly, bool autonomous = false,
        int maximum = 20, string? jsonPath = null) : MonicaTestApplicationFactory<ManualValues>
    {
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => [assembly];
        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ManualValues:Minimum"] = "10", ["ManualValues:Maximum"] = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ManualValues:Secret"] = "local-secret"
            });
            if (jsonPath is not null) builder.Configuration.AddJsonFile(jsonPath, optional: false, reloadOnChange: false);
        }
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => options.AutonomousWork = autonomous);
            builder.AddConfiguration(MonicaConfigurationInputPlan.Create(inputs =>
                inputs.UseFileConfigurationStore(options => options.RootDirectory = directory)),
                options => options.RuntimeValidationBehavior = ConfigurationRuntimeValidationBehavior.DiagnosticOnly);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

public class ManualValues : IValidatableObject
{
    public int Minimum { get; set; } = 10;
    public int Maximum { get; set; } = 20;
    [OptionSetting(IsSensitive = true)]
    public string Secret { get; set; } = "default-secret";
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Minimum > Maximum) yield return new ValidationResult("Minimum must not exceed Maximum.", [nameof(Minimum), nameof(Maximum)]);
    }
}
