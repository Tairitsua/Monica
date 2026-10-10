using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Reflection.Emit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Annotations;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Stores.File;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Exceptions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Configuration.Modules;

/// <summary>Exercises actual production discovery, first-seed activation, and Microsoft options creation.</summary>
public sealed class ManagedConfigurationObjectValidationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"monica-object-runtime-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(ConfigurationRuntimeValidationBehavior.DiagnosticOnly)]
    [InlineData(ConfigurationRuntimeValidationBehavior.FailFast)]
    public async Task OptionsCreation_WhenPostConfigureChangesAnOtherwiseValidSource_ShouldValidateTheActualInstance(
        ConfigurationRuntimeValidationBehavior behavior)
    {
        var factory = new ValidationApplicationFactory(_directory, behavior, postMaximum: 5);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var diagnostics = application.Services.GetRequiredService<IConfigurationOptionsValidationDiagnostics>();
        diagnostics.GetLatestReport(ValidationApplicationFactory.DEFINITION_KEY).Should().BeNull();
        var source = application.Services.GetRequiredService<IConfigurationRuntimeValidationService>()
            .GetReport(ValidationApplicationFactory.DEFINITION_KEY);
        source.IsValid.Should().BeTrue();

        if (behavior == ConfigurationRuntimeValidationBehavior.FailFast)
        {
            var act = () => factory.CreateOptions(application.Services);
            act.Should().Throw<TargetInvocationException>().Which.InnerException.Should().BeOfType<OptionsValidationException>();
        }
        else factory.CreateOptions(application.Services).Maximum.Should().Be(5);

        var report = diagnostics.GetLatestReport(ValidationApplicationFactory.DEFINITION_KEY)!;
        report.Coverage.Should().Be(ConfigurationValidationCoverage.Complete);
        report.Scope.Should().Be(ConfigurationValidationScope.ActualOptions);
        report.IsValid.Should().BeFalse();
        report.Issues.Should().OnlyContain(issue => issue.Kind == ConfigurationValidationIssueKind.Code
            && issue.EffectiveDisplayValue == null && issue.SourceChain.Values.Count == 0);
        application.Services.GetRequiredService<IConfigurationRuntimeValidationService>()
            .GetReport(ValidationApplicationFactory.DEFINITION_KEY).Should().BeSameAs(source);
        var attempt = report.AttemptId;
        factory.CreateOptions(application.Services, "independent-name");
        diagnostics.GetLatestReport(ValidationApplicationFactory.DEFINITION_KEY)!.AttemptId.Should().Be(attempt);
    }

    [Fact]
    public async Task FirstSeed_WhenDiagnosticOnlyAndObjectRuleFails_ShouldPersistAndReportInvalid()
    {
        var factory = new ValidationApplicationFactory(_directory, ConfigurationRuntimeValidationBehavior.DiagnosticOnly,
            sourceMaximum: 5);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var store = application.Services.GetRequiredService<IConfigurationEffectiveValueStore>();
        var document = await store.GetAsync(ValidationApplicationFactory.DEFINITION_KEY, TestContext.Current.CancellationToken);
        document.Should().NotBeNull();
        application.Services.GetRequiredService<IConfigurationRuntimeValidationService>()
            .GetReport(ValidationApplicationFactory.DEFINITION_KEY).IsValid.Should().BeFalse();
        factory.CreateOptions(application.Services).Maximum.Should().Be(5);
        application.Services.GetRequiredService<IConfigurationOptionsValidationDiagnostics>()
            .GetLatestReport(ValidationApplicationFactory.DEFINITION_KEY)!.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task FirstSeed_WhenFailFastAndObjectRuleFails_ShouldRejectBeforeEffectiveValuePersistence()
    {
        var factory = new ValidationApplicationFactory(_directory, ConfigurationRuntimeValidationBehavior.FailFast,
            sourceMaximum: 5);
        var act = () => factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ConfigurationRuntimeValidationException>();
        var store = new FileConfigurationStore(Options.Create(new ConfigurationFileStoreOptions { RootDirectory = _directory }));
        (await store.GetAsync(ValidationApplicationFactory.DEFINITION_KEY, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Startup_WhenAnExistingDocumentIsInvalid_ShouldLeaveItsValueAndVersionUntouched()
    {
        var first = new ValidationApplicationFactory(_directory, ConfigurationRuntimeValidationBehavior.DiagnosticOnly, sourceMaximum: 5);
        string original;
        await using (var application = await first.CreateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            original = (await application.Services.GetRequiredService<IConfigurationEffectiveValueStore>()
                .GetAsync(ValidationApplicationFactory.DEFINITION_KEY, TestContext.Current.CancellationToken))!.Json;
        }
        var second = new ValidationApplicationFactory(_directory, ConfigurationRuntimeValidationBehavior.FailFast);
        var act = () => second.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ConfigurationRuntimeValidationException>();
        var store = new FileConfigurationStore(Options.Create(new ConfigurationFileStoreOptions { RootDirectory = _directory }));
        var existing = (await store.GetAsync(ValidationApplicationFactory.DEFINITION_KEY, TestContext.Current.CancellationToken))!;
        existing.Json.Should().Be(original);
        existing.Version.Should().Be(1);
    }

    [Fact]
    public async Task Reload_WhenDiagnosticOnlyReceivesAnInvalidDocument_ShouldKeepCachedOptionsAndReportNewMonitorAttempt()
    {
        var factory = new ValidationApplicationFactory(_directory, ConfigurationRuntimeValidationBehavior.DiagnosticOnly);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var cached = factory.ReadOptions(application.Services, monitor: false);
        factory.ReadOptions(application.Services, monitor: true).Maximum.Should().Be(20);
        var definition = application.Services.GetRequiredService<IConfigurationDefinitionRegistry>().GetRequired(ValidationApplicationFactory.DEFINITION_KEY);
        var store = application.Services.GetRequiredService<IConfigurationEffectiveValueStore>();
        var current = (await store.GetAsync(definition.DefinitionKey, TestContext.Current.CancellationToken))!;
        // The low-level boundary supplies a changed document, as a reload can receive persisted external changes.
        await store.SaveAsync(new ConfigurationEffectiveValueSaveRequest
        {
            Definition = definition, ExpectedVersion = current.Version,
            Json = """{"Minimum":10,"Maximum":5,"Throws":false}"""
        }, TestContext.Current.CancellationToken);
        await application.Services.GetRequiredService<IConfigurationReloadCoordinator>()
            .ReloadMonicaProjectionAsync(definition.DefinitionKey, current.Version + 1, TestContext.Current.CancellationToken);

        factory.ReadOptions(application.Services, monitor: false).Should().BeSameAs(cached);
        cached.Maximum.Should().Be(20);
        factory.ReadOptions(application.Services, monitor: true).Maximum.Should().Be(5);
        application.Services.GetRequiredService<IConfigurationRuntimeValidationService>().GetReport(definition.DefinitionKey).IsValid.Should().BeFalse();
        application.Services.GetRequiredService<IConfigurationOptionsValidationDiagnostics>().GetLatestReport(definition.DefinitionKey)!.IsValid.Should().BeFalse();
        (await store.GetAsync(definition.DefinitionKey, TestContext.Current.CancellationToken))!.Version.Should().Be(2);
    }

    [Theory]
    [InlineData(ConfigurationRuntimeValidationBehavior.DiagnosticOnly)]
    [InlineData(ConfigurationRuntimeValidationBehavior.FailFast)]
    public async Task FirstSeed_WhenValidatorThrows_ShouldRejectBeforePersistenceForBothPolicies(
        ConfigurationRuntimeValidationBehavior behavior)
    {
        var factory = new ValidationApplicationFactory(_directory, behavior, sourceThrows: true);
        var act = () => factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var fault = (await act.Should().ThrowAsync<ConfigurationValidationExecutionException>()).Which;
        fault.InnerException.Should().BeNull();
        fault.ToString().Should().NotContain("synthetic-secret");
        var store = new FileConfigurationStore(Options.Create(new ConfigurationFileStoreOptions { RootDirectory = _directory }));
        (await store.GetAsync(ValidationApplicationFactory.DEFINITION_KEY, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Theory]
    [InlineData(ConfigurationRuntimeValidationBehavior.DiagnosticOnly)]
    [InlineData(ConfigurationRuntimeValidationBehavior.FailFast)]
    public async Task Composition_WhenOptionsOwnerHasNoPublicParameterlessConstructor_ShouldRejectBeforePersistenceForBothPolicies(
        ConfigurationRuntimeValidationBehavior behavior)
    {
        var factory = new ValidationApplicationFactory(_directory, behavior, publicConstructor: false);
        var act = () => factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var fault = (await act.Should().ThrowAsync<ModuleRegistrationException>()).Which;
        fault.Message.Should().Contain("options-constructor");
        fault.ToString().Should().NotContain("synthetic-secret");
        var store = new FileConfigurationStore(Options.Create(new ConfigurationFileStoreOptions { RootDirectory = _directory }));
        (await store.GetAsync(ValidationApplicationFactory.DEFINITION_KEY, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Theory]
    [InlineData(ConfigurationRuntimeValidationBehavior.DiagnosticOnly)]
    [InlineData(ConfigurationRuntimeValidationBehavior.FailFast)]
    public async Task OptionsCreation_WhenPostConfigureMakesCodeThrow_ShouldRecordSafeFailedAttemptAndThrowForBothPolicies(
        ConfigurationRuntimeValidationBehavior behavior)
    {
        var factory = new ValidationApplicationFactory(_directory, behavior, postThrows: true);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var act = () => factory.CreateOptions(application.Services);
        var fault = act.Should().Throw<TargetInvocationException>().Which.InnerException;
        fault.Should().BeOfType<ConfigurationValidationExecutionException>();
        fault!.ToString().Should().NotContain("synthetic-secret");
        var report = application.Services.GetRequiredService<IConfigurationOptionsValidationDiagnostics>()
            .GetLatestReport(ValidationApplicationFactory.DEFINITION_KEY)!;
        report.Coverage.Should().Be(ConfigurationValidationCoverage.Failed);
        report.IsSchemaValid.Should().BeFalse();
        report.Issues.Should().ContainSingle().Which.Kind.Should().Be(ConfigurationValidationIssueKind.Execution);
    }

    [Theory]
    [InlineData(ConfigurationRuntimeValidationBehavior.DiagnosticOnly, 1)]
    [InlineData(ConfigurationRuntimeValidationBehavior.DiagnosticOnly, 2)]
    [InlineData(ConfigurationRuntimeValidationBehavior.DiagnosticOnly, 3)]
    [InlineData(ConfigurationRuntimeValidationBehavior.FailFast, 1)]
    [InlineData(ConfigurationRuntimeValidationBehavior.FailFast, 2)]
    [InlineData(ConfigurationRuntimeValidationBehavior.FailFast, 3)]
    public async Task FirstSeed_WhenUserCodeThrowsAForgedFrameworkFault_ShouldRejectSafelyBeforePersistence(
        ConfigurationRuntimeValidationBehavior behavior, int faultMode)
    {
        var factory = new ValidationApplicationFactory(_directory, behavior, sourceForgedFaultMode: faultMode);
        var act = () => factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        AssertSafeObjectRuleFault((await act.Should().ThrowAsync<ConfigurationValidationExecutionException>()).Which);
        var store = new FileConfigurationStore(Options.Create(new ConfigurationFileStoreOptions { RootDirectory = _directory }));
        (await store.GetAsync(ValidationApplicationFactory.DEFINITION_KEY, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Theory]
    [InlineData(ConfigurationRuntimeValidationBehavior.DiagnosticOnly, 1)]
    [InlineData(ConfigurationRuntimeValidationBehavior.DiagnosticOnly, 2)]
    [InlineData(ConfigurationRuntimeValidationBehavior.DiagnosticOnly, 3)]
    [InlineData(ConfigurationRuntimeValidationBehavior.FailFast, 1)]
    [InlineData(ConfigurationRuntimeValidationBehavior.FailFast, 2)]
    [InlineData(ConfigurationRuntimeValidationBehavior.FailFast, 3)]
    public async Task SuppliedInstanceAndOptionsCreation_WhenUserCodeForgesAFault_ShouldKeepSafeCreationOnlyDiagnostics(
        ConfigurationRuntimeValidationBehavior behavior, int faultMode)
    {
        var factory = new ValidationApplicationFactory(_directory, behavior, postForgedFaultMode: faultMode);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var diagnostics = application.Services.GetRequiredService<IConfigurationOptionsValidationDiagnostics>();
        var source = application.Services.GetRequiredService<IConfigurationRuntimeValidationService>()
            .GetReport(ValidationApplicationFactory.DEFINITION_KEY);
        source.IsValid.Should().BeTrue();
        var supplied = factory.CreateSuppliedInstance();
        supplied.ForgedFaultMode = faultMode;
        var validate = () => factory.ValidateSuppliedInstance(application.Services, supplied);

        AssertSafeObjectRuleFault(validate.Should().Throw<TargetInvocationException>().Which.InnerException
            .Should().BeOfType<ConfigurationValidationExecutionException>().Which);
        supplied.ForgedFaultMode.Should().Be(faultMode);
        diagnostics.GetReports().Should().BeEmpty();

        var create = () => factory.CreateOptions(application.Services);
        AssertSafeObjectRuleFault(create.Should().Throw<TargetInvocationException>().Which.InnerException
            .Should().BeOfType<ConfigurationValidationExecutionException>().Which);
        var observed = diagnostics.GetLatestReport(ValidationApplicationFactory.DEFINITION_KEY)!;
        observed.Coverage.Should().Be(ConfigurationValidationCoverage.Failed);
        var issue = observed.Issues.Should().ContainSingle().Which;
        issue.Kind.Should().Be(ConfigurationValidationIssueKind.Execution);
        issue.LogicalPath.Should().Be(LogicalPath.Root);
        issue.Problem.Should().NotContain("synthetic-secret");
        issue.EffectiveDisplayValue.Should().BeNull();
        issue.SourceChain.Values.Should().BeEmpty();

        AssertSafeObjectRuleFault(validate.Should().Throw<TargetInvocationException>().Which.InnerException
            .Should().BeOfType<ConfigurationValidationExecutionException>().Which);
        diagnostics.GetLatestReport(ValidationApplicationFactory.DEFINITION_KEY).Should().BeSameAs(observed);
        application.Services.GetRequiredService<IConfigurationRuntimeValidationService>()
            .GetReport(ValidationApplicationFactory.DEFINITION_KEY).Should().BeSameAs(source);
    }

    private static void AssertSafeObjectRuleFault(ConfigurationValidationExecutionException fault)
    {
        fault.Kind.Should().Be(ConfigurationValidationIssueKind.Execution);
        fault.Stage.Should().Be("object-rule");
        fault.DefinitionKey.Should().Be(ValidationApplicationFactory.DEFINITION_KEY);
        fault.LogicalPath.Should().Be(LogicalPath.Root);
        fault.InnerException.Should().BeNull();
        fault.ToString().Should().NotContain("synthetic-secret");
    }

    [Theory]
    [InlineData(ConfigurationRuntimeValidationBehavior.DiagnosticOnly)]
    [InlineData(ConfigurationRuntimeValidationBehavior.FailFast)]
    public async Task ValidateSuppliedInstance_WhenItDiffersFromSourceAndPostConfigure_ShouldReportWithoutPolicyOrCreationSideEffects(
        ConfigurationRuntimeValidationBehavior behavior)
    {
        var factory = new ValidationApplicationFactory(_directory, behavior, postMaximum: 25);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var diagnostics = application.Services.GetRequiredService<IConfigurationOptionsValidationDiagnostics>();
        var source = application.Services.GetRequiredService<IConfigurationRuntimeValidationService>()
            .GetReport(ValidationApplicationFactory.DEFINITION_KEY);
        source.IsValid.Should().BeTrue();
        diagnostics.GetLatestReport(ValidationApplicationFactory.DEFINITION_KEY).Should().BeNull();
        var supplied = factory.CreateSuppliedInstance();
        supplied.Maximum = 5;

        var report = factory.ValidateSuppliedInstance(application.Services, supplied);

        report.Scope.Should().Be(ConfigurationValidationScope.ActualOptions);
        report.Coverage.Should().Be(ConfigurationValidationCoverage.Complete);
        report.IsValid.Should().BeFalse();
        report.DefinitionReports.Should().ContainSingle().Which.DefinitionKey.Should().Be(ValidationApplicationFactory.DEFINITION_KEY);
        report.Issues.Should().OnlyContain(issue => issue.Kind == ConfigurationValidationIssueKind.Code
            && issue.EffectiveDisplayValue == null && issue.SourceChain.Values.Count == 0);
        supplied.Minimum.Should().Be(10);
        supplied.Maximum.Should().Be(5);
        supplied.Throws.Should().BeFalse();
        diagnostics.GetReports().Should().BeEmpty();
        application.Services.GetRequiredService<IConfigurationRuntimeValidationService>()
            .GetReport(ValidationApplicationFactory.DEFINITION_KEY).Should().BeSameAs(source);

        factory.CreateOptions(application.Services).Maximum.Should().Be(25);
        var observed = diagnostics.GetLatestReport(ValidationApplicationFactory.DEFINITION_KEY)!;
        observed.IsValid.Should().BeTrue();
        factory.ValidateSuppliedInstance(application.Services, supplied).IsValid.Should().BeFalse();
        diagnostics.GetLatestReport(ValidationApplicationFactory.DEFINITION_KEY).Should().BeSameAs(observed);

        supplied.Throws = true;
        var act = () => factory.ValidateSuppliedInstance(application.Services, supplied);
        var fault = act.Should().Throw<TargetInvocationException>().Which.InnerException
            .Should().BeOfType<ConfigurationValidationExecutionException>().Which;
        fault.Kind.Should().Be(ConfigurationValidationIssueKind.Execution);
        fault.InnerException.Should().BeNull();
        fault.ToString().Should().NotContain("synthetic-secret");
        diagnostics.GetLatestReport(ValidationApplicationFactory.DEFINITION_KEY).Should().BeSameAs(observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateSuppliedInstance_WhenItsClrTypeHasNoLocalOwner_ShouldRejectWithoutExecutingPublishedCode(
        bool publishMetadata)
    {
        var factory = new ValidationApplicationFactory(_directory, ConfigurationRuntimeValidationBehavior.DiagnosticOnly);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var registry = application.Services.GetRequiredService<IConfigurationDefinitionRegistry>();
        if (publishMetadata)
            registry.Register(registry.GetRequired(ValidationApplicationFactory.DEFINITION_KEY) with
            {
                DefinitionKey = "test.published-only-owner",
                ClrTypeName = typeof(RuntimeObjectValues).AssemblyQualifiedName!,
                Origin = ConfigurationDefinitionOrigin.PublishedMetadata
            });
        var validator = application.Services.GetRequiredService<IConfigurationOptionsInstanceValidationService>();
        // This base instance would throw if execution were authorized by published metadata or assignability.
        var act = () => validator.Validate(new RuntimeObjectValues { Throws = true });

        var fault = act.Should().Throw<ConfigurationValidationExecutionException>().Which;
        fault.Kind.Should().Be(ConfigurationValidationIssueKind.Contract);
        fault.Stage.Should().Be("local-options-owner");
        fault.InnerException.Should().BeNull();
        fault.ToString().Should().NotContain("synthetic-secret");
        application.Services.GetRequiredService<IConfigurationOptionsValidationDiagnostics>().GetReports().Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class ValidationApplicationFactory(
        string directory,
        ConfigurationRuntimeValidationBehavior behavior,
        int sourceMaximum = 20,
        int? postMaximum = null,
        bool sourceThrows = false,
        bool postThrows = false,
        bool publicConstructor = true,
        int sourceForgedFaultMode = 0,
        int postForgedFaultMode = 0) : MonicaTestApplicationFactory<RuntimeObjectValues>
    {
        internal const string DEFINITION_KEY = "test.runtime-object-validation";
        private readonly (Assembly Assembly, Type OptionsType) _discovery = CreateDiscoveryAssembly(publicConstructor);

        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => [_discovery.Assembly];

        protected override void ConfigureHost(WebApplicationBuilder builder)
            => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RuntimeObject:Minimum"] = "10", ["RuntimeObject:Maximum"] = sourceMaximum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["RuntimeObject:Throws"] = sourceThrows ? "true" : "false",
                ["RuntimeObject:ForgedFaultMode"] = sourceForgedFaultMode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            var plan = MonicaConfigurationInputPlan.Create(inputs => inputs.UseFileConfigurationStore(
                options => options.RootDirectory = directory));
            builder.AddConfiguration(plan, options => options.RuntimeValidationBehavior = behavior);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            typeof(ValidationApplicationFactory).GetMethod(nameof(ConfigureActualOptions), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(_discovery.OptionsType).Invoke(this, [services]);
        }

        private void ConfigureActualOptions<TOptions>(IServiceCollection services) where TOptions : RuntimeObjectValues
            => services.PostConfigure<TOptions>(options =>
            {
                if (postMaximum is { } maximum) options.Maximum = maximum;
                if (postThrows) options.Throws = true;
                if (postForgedFaultMode != 0) options.ForgedFaultMode = postForgedFaultMode;
            });

        internal RuntimeObjectValues CreateOptions(IServiceProvider services, string name = "")
        {
            var type = typeof(IOptionsFactory<>).MakeGenericType(_discovery.OptionsType);
            return (RuntimeObjectValues)type.GetMethod(nameof(IOptionsFactory<object>.Create))!
                .Invoke(services.GetRequiredService(type), [name])!;
        }

        internal RuntimeObjectValues ReadOptions(IServiceProvider services, bool monitor)
        {
            var type = (monitor ? typeof(IOptionsMonitor<>) : typeof(IOptions<>)).MakeGenericType(_discovery.OptionsType);
            return (RuntimeObjectValues)type.GetProperty(monitor ? "CurrentValue" : "Value")!.GetValue(services.GetRequiredService(type))!;
        }

        internal RuntimeObjectValues CreateSuppliedInstance() => (RuntimeObjectValues)Activator.CreateInstance(_discovery.OptionsType)!;

        internal ConfigurationValidationReport ValidateSuppliedInstance(IServiceProvider services, RuntimeObjectValues options)
            => (ConfigurationValidationReport)typeof(IConfigurationOptionsInstanceValidationService)
                .GetMethod(nameof(IConfigurationOptionsInstanceValidationService.Validate))!
                .MakeGenericMethod(_discovery.OptionsType)
                .Invoke(services.GetRequiredService<IConfigurationOptionsInstanceValidationService>(), [options])!;

        private static (Assembly Assembly, Type OptionsType) CreateDiscoveryAssembly(bool publicConstructor)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"Test.RuntimeObject.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("Main").DefineType("RuntimeObjectOptions",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, typeof(RuntimeObjectValues));
            type.DefineDefaultConstructor(publicConstructor ? MethodAttributes.Public : MethodAttributes.Private);
            type.SetCustomAttribute(new CustomAttributeBuilder(typeof(ConfigurationAttribute).GetConstructor([typeof(string)])!,
                ["RuntimeObject"], [typeof(ConfigurationAttribute).GetProperty(nameof(ConfigurationAttribute.DefinitionKey))!], [DEFINITION_KEY]));
            return (assembly, type.CreateType()!);
        }
    }
}

/// <summary>Provides a real pure value rule inherited by each isolated host's discovered options owner.</summary>
public class RuntimeObjectValues : IValidatableObject
{
    public int Minimum { get; set; } = 10;
    public int Maximum { get; set; } = 20;
    public bool Throws { get; set; }
    public int ForgedFaultMode { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Throws) throw new InvalidOperationException("synthetic-secret");
        if (ForgedFaultMode == 1) throw ForgedFault();
        return EnumerateResults();
    }

    private IEnumerable<ValidationResult> EnumerateResults()
    {
        if (ForgedFaultMode == 2) throw ForgedFault();
        if (ForgedFaultMode == 3) yield return new ValidationResult("Rule failure.", EnumerateFaultingMembers());
        if (Minimum > Maximum) yield return new ValidationResult("Minimum must not exceed Maximum.", [nameof(Minimum), nameof(Maximum)]);
    }

    private static IEnumerable<string> EnumerateFaultingMembers()
    {
        yield return nameof(Minimum);
        throw ForgedFault();
    }

    private static ConfigurationValidationExecutionException ForgedFault() => new("synthetic-secret-owner",
        LogicalPath.Root.Append(new PropertySegment("synthetic-secret-path")), "synthetic-secret",
        ConfigurationValidationIssueKind.Contract);
}
