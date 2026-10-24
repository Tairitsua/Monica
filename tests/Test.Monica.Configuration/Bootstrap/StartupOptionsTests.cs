using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Reflection.Emit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Annotations;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Exceptions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Configuration.Bootstrap;

/// <summary>Exercises deployment-owned options through complete hosts without a managed configuration store.</summary>
public sealed class StartupOptionsTests
{
    [Fact]
    public async Task GetStartupOptions_WhenReadRepeatedlyAndResolvedAcrossScopes_ShouldBindAndValidateOneFrozenInstance()
    {
        var factory = new StartupApplicationFactory(changeDuringComposition: true);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var actual = factory.EarlyValue!;
        var access = factory.Access(application.Services);

        actual.Endpoint.Should().Be("https://initial.test");
        actual.EndpointAssignments.Should().Be(1);
        actual.ValidationCalls.Should().Be(1);
        factory.RepeatedEarlyValue.Should().BeSameAs(actual);
        access.Options.Should().BeSameAs(actual);
        access.Monitor.Should().BeSameAs(actual);
        access.Create(Options.DefaultName).Should().BeSameAs(actual);
        access.GetMonitor(null).Should().BeSameAs(actual);
        await using (var first = application.CreateScope(TestContext.Current.CancellationToken))
        await using (var second = application.CreateScope(TestContext.Current.CancellationToken))
        {
            factory.Access(first.ServiceProvider).Snapshot.Should().BeSameAs(actual);
            factory.Access(second.ServiceProvider).GetSnapshot(Options.DefaultName).Should().BeSameAs(actual);
        }

        var callbacks = 0;
        using var subscription = access.OnChange(() => callbacks++);
        factory.SetCandidate(nameof(StartupOptionsValues.Endpoint), "https://candidate.test");
        factory.Reload();

        access.Options.Should().BeSameAs(actual);
        access.Monitor.Endpoint.Should().Be("https://initial.test");
        actual.EndpointAssignments.Should().Be(1);
        actual.ValidationCalls.Should().Be(1);
        callbacks.Should().Be(0);
    }

    [Theory]
    [InlineData(true, "0")]
    [InlineData(false, "0")]
    [InlineData(true, "invalid-startup-value")]
    [InlineData(false, "invalid-startup-value")]
    public async Task Composition_WhenStartupInputIsInvalid_ShouldFailBeforeHostStartupWithoutDisclosingTheValue(
        bool readEarly, string attempts)
    {
        var factory = new StartupApplicationFactory(readEarly: readEarly, attempts: attempts);
        Func<Task> compose = async () =>
        {
            await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        };

        var failure = await compose.Should().ThrowAsync<Exception>();

        failure.Which.ToString().Should().NotContain("invalid-startup-value");
        if (readEarly)
        {
            EnumerateFailures(failure.Which).Any(exception =>
                exception is ConfigurationValidationExecutionException execution
                    && execution.DefinitionKey == StartupApplicationFactory.DEFINITION_KEY
                || exception is ConfigurationRuntimeValidationException validation
                    && validation.Report.DefinitionReports.Any(report => report.DefinitionKey == StartupApplicationFactory.DEFINITION_KEY))
                .Should().BeTrue("the failure must belong to the startup definition");
        }
        else
        {
            failure.Which.Should().BeOfType<ModuleRegistrationException>();
            failure.Which.Message.Should().Contain("[startup-options] OptionsStartupFixture")
                .And.Contain("Configuration key: StartupFixture");
        }
        factory.ConfigurationCallbackCompleted.Should().Be(!readEarly);
        factory.ServicesConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Composition_WhenStartupInputIsAbsent_ShouldUseValidClrDefaults()
    {
        var factory = new StartupApplicationFactory(endpoint: null, attempts: null, readEarly: false);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var actual = factory.Access(application.Services).Options;

        actual.Endpoint.Should().Be("https://default.test");
        actual.Attempts.Should().Be(3);
        actual.EndpointAssignments.Should().Be(0);
        actual.ValidationCalls.Should().Be(1);
    }

    [Fact]
    public async Task OptionsAdapters_WhenANondefaultNameIsRequested_ShouldRejectIt()
    {
        var factory = new StartupApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var access = factory.Access(scope.ServiceProvider);

        Action snapshot = () => access.GetSnapshot("named");
        Action monitor = () => access.GetMonitor("named");
        Action create = () => access.Create("named");

        snapshot.Should().Throw<InvalidOperationException>().WithMessage("*named options*");
        monitor.Should().Throw<InvalidOperationException>().WithMessage("*named options*");
        create.Should().Throw<InvalidOperationException>().WithMessage("*named options*");
        access.GetSnapshot(null).Should().BeSameAs(factory.EarlyValue);
    }

    [Theory]
    [InlineData(OptionsConflict.Configure)]
    [InlineData(OptionsConflict.PostConfigure)]
    [InlineData(OptionsConflict.NamedConfigure)]
    [InlineData(OptionsConflict.ClosedOptions)]
    [InlineData(OptionsConflict.ClosedFactory)]
    [InlineData(OptionsConflict.OpenOptions)]
    [InlineData(OptionsConflict.OpenSnapshot)]
    [InlineData(OptionsConflict.OpenMonitor)]
    [InlineData(OptionsConflict.OpenFactory)]
    public async Task Composition_WhenOrdinaryOptionsServicesConflictWithStartupOwnership_ShouldRejectTheRegistration(
        OptionsConflict conflict)
    {
        var factory = new StartupApplicationFactory(conflict: conflict);
        Func<Task> compose = async () =>
        {
            await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        };

        var failure = await compose.Should().ThrowAsync<Exception>();

        failure.Which.ToString().Should().Contain("cannot use named options");
        factory.ConfigureCallbacks.Should().Be(0);
        factory.ServicesConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Composition_WhenPostServiceRegistrationAddsAnOptionsContribution_ShouldRejectItAtFinalization()
    {
        var factory = new StartupApplicationFactory(conflict: OptionsConflict.Configure, lateConflict: true);
        Func<Task> compose = async () =>
        {
            await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        };

        var failure = await compose.Should().ThrowAsync<Exception>();

        factory.LateConflictRegistered.Should().BeTrue();
        failure.Which.ToString().Should().Contain("cannot use named options");
        factory.ConfigureCallbacks.Should().Be(0);
    }

    [Theory]
    [InlineData(OptionsConflict.ClosedOptions)]
    [InlineData(OptionsConflict.Configure)]
    public async Task Composition_WhenALaterFinalizerOverridesStartupOptions_ShouldRejectItAfterAllAdaptersAreRegistered(
        OptionsConflict conflict)
    {
        var factory = new StartupApplicationFactory(conflict: conflict, finalizerConflict: true);
        Func<Task> compose = async () =>
        {
            await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        };

        var failure = await compose.Should().ThrowAsync<Exception>();

        factory.LateConflictRegistered.Should().BeTrue();
        failure.Which.ToString().Should().Contain("cannot use named options");
        factory.ConfigureCallbacks.Should().Be(0);
        factory.ServicesConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Composition_WhenALaterFinalizerRemovesAFrozenAdapter_ShouldRejectTheOpenGenericFallback()
    {
        var factory = new StartupApplicationFactory(conflict: OptionsConflict.RemoveOptions, finalizerConflict: true);
        Func<Task> compose = async () =>
        {
            await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        };

        var failure = await compose.Should().ThrowAsync<Exception>();

        factory.LateConflictRegistered.Should().BeTrue();
        failure.Which.ToString().Should().Contain("require their frozen IOptions");
        factory.ServicesConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Diagnostics_WhenOnlyStartupDefinitionsExist_ShouldWorkWithoutAnyManagedStore()
    {
        var factory = new StartupApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        application.Services.GetService<IConfigurationEffectiveValueStore>().Should().BeNull();
        application.Services.GetService<IConfigurationMetadataStore>().Should().BeNull();
        application.Services.GetService<IConfigurationHistoryStore>().Should().BeNull();
        var facade = application.Services.GetRequiredService<ConfigurationFacade>();

        var definitions = RequireSuccess(await facade.GetDefinitionsAsync());
        var catalog = RequireSuccess(await facade.GetDefinitionCatalogAsync());
        var summary = catalog.Definitions.Should().ContainSingle().Which;
        definitions.Should().ContainSingle();
        summary.Kind.Should().Be(ConfigurationKind.Startup);
        summary.CanManage.Should().BeTrue();
        summary.CanMutate.Should().BeFalse();
        summary.ReadOnlyReason.Should().NotBeNullOrEmpty();
        var state = RequireSuccess(await facade.GetDefinitionStateAsync(StartupApplicationFactory.DEFINITION_KEY));
        state.EffectiveValueVersion.Should().BeNull();
        state.StartupCandidate!.ValidationReport.IsValid.Should().BeTrue();
        state.StartupCandidate.RequiresRestart.Should().BeFalse();
        RequireSuccess(await facade.GetDefinitionStatesAsync()).Should().ContainSingle();
        var chains = RequireSuccess(await facade.GetSourceChainsAsync(StartupApplicationFactory.DEFINITION_KEY,
            [LogicalPath.Root, LogicalPath.FromProperties(nameof(StartupOptionsValues.Endpoint))]));
        chains.Should().HaveCount(2).And.AllSatisfy(chain =>
        {
            chain.Kind.Should().Be(ConfigurationKind.Startup);
            chain.StartupActualValue.Should().NotBeNull();
            chain.StartupValues.Should().NotBeEmpty();
            chain.Values.Should().NotBeEmpty();
        });
        RequireSuccess(await facade.GetRuntimeValidationReportAsync(TestContext.Current.CancellationToken))
            .IsValid.Should().BeTrue();
        var reloadStatus = RequireSuccess(await facade.GetRuntimeReloadStatusAsync());
        reloadStatus.EffectiveValueStore.Should().BeNull();
        reloadStatus.Definitions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("invalid-candidate-value")]
    public async Task Diagnostics_WhenLiveCandidateChangesOrFailsValidation_ShouldKeepTheActualValueFrozen(string invalidValue)
    {
        var factory = new StartupApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var facade = application.Services.GetRequiredService<ConfigurationFacade>();
        var path = LogicalPath.FromProperties(nameof(StartupOptionsValues.Attempts));
        var actual = factory.EarlyValue!;

        factory.SetCandidate(nameof(StartupOptionsValues.Attempts), "7");
        var changed = RequireSuccess(await facade.GetEffectiveValueAsync(StartupApplicationFactory.DEFINITION_KEY, path));
        changed.DisplayValue.Should().Be("3");
        changed.StartupCandidate!.DisplayValue.Should().Be("7");
        changed.StartupCandidate.RequiresRestart.Should().BeTrue();
        changed.StartupCandidate.ValidationReport.IsValid.Should().BeTrue();
        var chain = RequireSuccess(await facade.GetSourceChainAsync(StartupApplicationFactory.DEFINITION_KEY, path));
        chain.StartupValues.Single(value => value.IsEffective).DisplayValue.Should().Be("3");
        chain.Values.Single(value => value.IsEffective).DisplayValue.Should().Be("7");

        factory.SetCandidate(nameof(StartupOptionsValues.Attempts), invalidValue);
        var invalid = RequireSuccess(await facade.GetDefinitionStateAsync(StartupApplicationFactory.DEFINITION_KEY));
        invalid.StartupCandidate!.RequiresRestart.Should().BeTrue();
        invalid.StartupCandidate.ValidationReport.IsValid.Should().BeFalse();
        invalid.StartupCandidate.ValidationReport.Issues.Should().NotBeEmpty().And.AllSatisfy(issue =>
        {
            issue.EffectiveDisplayValue.Should().BeNull();
            issue.Problem.Should().NotContain("invalid-candidate-value");
        });
        invalid.EffectiveValues.Single(value => value.LogicalPath.Equals(path)).DisplayValue.Should().Be("3");
        factory.Access(application.Services).Options.Should().BeSameAs(actual);
        actual.Attempts.Should().Be(3);
        actual.ValidationCalls.Should().Be(1);
        RequireSuccess(await facade.GetRuntimeValidationReportAsync(TestContext.Current.CancellationToken))
            .IsValid.Should().BeTrue();

        factory.SetCandidate(nameof(StartupOptionsValues.Attempts), "3");
        var restored = RequireSuccess(await facade.GetDefinitionStateAsync(StartupApplicationFactory.DEFINITION_KEY));
        restored.StartupCandidate!.RequiresRestart.Should().BeFalse();
        restored.StartupCandidate.ValidationReport.IsValid.Should().BeTrue();
    }

    private static T RequireSuccess<T>(Res<T> result) where T : class
    {
        result.Status.Should().Be(ResStatus.Ok, "{0}", result.Message);
        result.Message.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();
        return result.Data!;
    }

    private static IEnumerable<Exception> EnumerateFailures(Exception exception)
    {
        yield return exception;
        IEnumerable<Exception> causes = exception is AggregateException aggregate ? aggregate.InnerExceptions
            : exception.InnerException is { } cause ? new[] { cause } : [];
        foreach (var nested in causes.SelectMany(EnumerateFailures)) yield return nested;
    }

    public enum OptionsConflict
    {
        None, Configure, PostConfigure, NamedConfigure, ClosedOptions, ClosedFactory,
        OpenOptions, OpenSnapshot, OpenMonitor, OpenFactory, RemoveOptions
    }

    private sealed class StartupApplicationFactory(
        bool readEarly = true,
        string? endpoint = "https://initial.test",
        string? attempts = "3",
        bool changeDuringComposition = false,
        OptionsConflict conflict = OptionsConflict.None,
        bool lateConflict = false,
        bool finalizerConflict = false) : MonicaTestApplicationFactory<StartupOptionsValues>
    {
        internal const string DEFINITION_KEY = "test.startup-options";
        private readonly (Assembly Assembly, Type Type) _owner = CreateOwner();
        private readonly string _inputId = Guid.NewGuid().ToString("N");
        private IConfigurationRoot _configuration = null!;

        internal StartupOptionsValues? EarlyValue { get; private set; }
        internal StartupOptionsValues? RepeatedEarlyValue { get; private set; }
        internal bool ConfigurationCallbackCompleted { get; private set; }
        internal bool ServicesConfigured { get; private set; }
        internal bool LateConflictRegistered { get; private set; }
        internal int ConfigureCallbacks { get; private set; }
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => [_owner.Assembly];

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            builder.Environment.EnvironmentName = Environments.Development;
            builder.Host.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });
            var values = new Dictionary<string, string?> { ["StartupProbe:Input"] = _inputId };
            if (endpoint is not null) values["StartupFixture:Endpoint"] = endpoint;
            if (attempts is not null) values["StartupFixture:Attempts"] = attempts;
            builder.Configuration.AddInMemoryCollection(values);
            _configuration = builder.Configuration;
            if (!lateConflict && !finalizerConflict) RegisterConflict(builder.Services);
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddConfiguration();
            if (changeDuringComposition) SetCandidate(nameof(StartupOptionsValues.Endpoint), "https://composition.test");
            if (readEarly)
            {
                EarlyValue = ReadEarly(builder);
                RepeatedEarlyValue = ReadEarly(builder);
            }
            if (lateConflict)
                builder.AddModule<StartupConflictModule, StartupConflictModuleOptions>(options =>
                    options.Register = services =>
                    {
                        RegisterConflict(services);
                        LateConflictRegistered = true;
                    });
            if (finalizerConflict)
                builder.Composition.OnServicesFinalized(services =>
                {
                    RegisterConflict(services);
                    LateConflictRegistered = true;
                });
            ConfigurationCallbackCompleted = true;
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            ServicesConfigured = true;
        }

        internal void SetCandidate(string property, string value)
            => _configuration.Providers.OfType<MemoryConfigurationProvider>().Single(provider =>
                provider.TryGet("StartupProbe:Input", out var input) && input == _inputId)
                .Set($"StartupFixture:{property}", value);

        internal void Reload() => _configuration.Reload();

        internal IStartupOptionsAccess Access(IServiceProvider services)
            => (IStartupOptionsAccess)typeof(StartupApplicationFactory)
                .GetMethod(nameof(CreateAccess), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(_owner.Type).Invoke(null, [services])!;

        private static IStartupOptionsAccess CreateAccess<TOptions>(IServiceProvider services)
            where TOptions : StartupOptionsValues, new() => new StartupOptionsAccess<TOptions>(services);

        private StartupOptionsValues ReadEarly(IMonicaBuilder builder)
            => (StartupOptionsValues)typeof(MonicaStartupOptionsExtensions)
                .GetMethod(nameof(MonicaStartupOptionsExtensions.GetStartupOptions))!
                .MakeGenericMethod(_owner.Type).Invoke(null, [builder])!;

        private void RegisterConflict(IServiceCollection services)
        {
            if (conflict == OptionsConflict.None) return;
            typeof(StartupApplicationFactory).GetMethod(nameof(RegisterConflictFor), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(_owner.Type).Invoke(this, [services]);
        }

        private void RegisterConflictFor<TOptions>(IServiceCollection services) where TOptions : class, new()
        {
            switch (conflict)
            {
                case OptionsConflict.Configure: services.Configure<TOptions>(_ => ConfigureCallbacks++); break;
                case OptionsConflict.PostConfigure: services.PostConfigure<TOptions>(_ => ConfigureCallbacks++); break;
                case OptionsConflict.NamedConfigure: services.Configure<TOptions>("named", _ => ConfigureCallbacks++); break;
                case OptionsConflict.ClosedOptions: services.AddSingleton<IOptions<TOptions>>(Options.Create(new TOptions())); break;
                case OptionsConflict.ClosedFactory: services.AddSingleton<IOptionsFactory<TOptions>, ReplacementOptionsFactory<TOptions>>(); break;
                case OptionsConflict.OpenOptions: services.AddSingleton(typeof(IOptions<>), typeof(ReplacementOptions<>)); break;
                case OptionsConflict.OpenSnapshot: services.AddScoped(typeof(IOptionsSnapshot<>), typeof(ReplacementOptions<>)); break;
                case OptionsConflict.OpenMonitor: services.AddSingleton(typeof(IOptionsMonitor<>), typeof(ReplacementOptions<>)); break;
                case OptionsConflict.OpenFactory: services.AddTransient(typeof(IOptionsFactory<>), typeof(ReplacementOptionsFactory<>)); break;
                case OptionsConflict.RemoveOptions: services.RemoveAll<IOptions<TOptions>>(); break;
            }
        }

        private static (Assembly Assembly, Type Type) CreateOwner()
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"Test.StartupOptions.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
            var owner = assembly.DefineDynamicModule("Main").DefineType("OptionsStartupFixture",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, typeof(StartupOptionsValues));
            owner.DefineDefaultConstructor(MethodAttributes.Public);
            owner.SetCustomAttribute(new CustomAttributeBuilder(typeof(ConfigurationAttribute).GetConstructor([typeof(string)])!,
                ["StartupFixture"],
                [typeof(ConfigurationAttribute).GetProperty(nameof(ConfigurationAttribute.DefinitionKey))!,
                    typeof(ConfigurationAttribute).GetProperty(nameof(ConfigurationAttribute.Kind))!],
                [DEFINITION_KEY, ConfigurationKind.Startup]));
            return (assembly, owner.CreateType()!);
        }
    }

    private interface IStartupOptionsAccess
    {
        StartupOptionsValues Options { get; }
        StartupOptionsValues Snapshot { get; }
        StartupOptionsValues Monitor { get; }
        StartupOptionsValues GetSnapshot(string? name);
        StartupOptionsValues GetMonitor(string? name);
        StartupOptionsValues Create(string name);
        IDisposable? OnChange(Action callback);
    }

    private sealed class StartupOptionsAccess<TOptions>(IServiceProvider services) : IStartupOptionsAccess
        where TOptions : StartupOptionsValues, new()
    {
        public StartupOptionsValues Options => services.GetRequiredService<IOptions<TOptions>>().Value;
        public StartupOptionsValues Snapshot => services.GetRequiredService<IOptionsSnapshot<TOptions>>().Value;
        public StartupOptionsValues Monitor => services.GetRequiredService<IOptionsMonitor<TOptions>>().CurrentValue;
        public StartupOptionsValues GetSnapshot(string? name) => services.GetRequiredService<IOptionsSnapshot<TOptions>>().Get(name);
        public StartupOptionsValues GetMonitor(string? name) => services.GetRequiredService<IOptionsMonitor<TOptions>>().Get(name);
        public StartupOptionsValues Create(string name) => services.GetRequiredService<IOptionsFactory<TOptions>>().Create(name);
        public IDisposable? OnChange(Action callback) => services.GetRequiredService<IOptionsMonitor<TOptions>>().OnChange((_, _) => callback());
    }

    private sealed class ReplacementOptionsFactory<TOptions> : IOptionsFactory<TOptions> where TOptions : class, new()
    {
        public ReplacementOptionsFactory() { }
        public TOptions Create(string name) => new();
    }

    private sealed class ReplacementOptions<TOptions> : IOptions<TOptions>, IOptionsSnapshot<TOptions>, IOptionsMonitor<TOptions>
        where TOptions : class, new()
    {
        public ReplacementOptions() { }
        public TOptions Value { get; } = new();
        public TOptions CurrentValue => Value;
        public TOptions Get(string? name) => Value;
        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }

    public sealed class StartupConflictModule : MonicaModule<StartupConflictModuleOptions>
    {
        public override void PostConfigureServices(ModuleContext<StartupConflictModuleOptions> context)
            => Option.Register?.Invoke(context.Services);
    }

    public sealed class StartupConflictModuleOptions : ModuleOptions<StartupConflictModule>
    {
        public Action<IServiceCollection>? Register { get; set; }
    }
}

/// <summary>Supplies per-instance binding and validation observations to isolated discovered option owners.</summary>
public class StartupOptionsValues : IValidatableObject
{
    private string _endpoint = "https://default.test";

    [Required]
    public string Endpoint
    {
        get => _endpoint;
        set
        {
            _endpoint = value;
            EndpointAssignments++;
        }
    }

    [Range(1, 10)]
    public int Attempts { get; set; } = 3;

    internal int EndpointAssignments { get; private set; }
    internal int ValidationCalls { get; private set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        ValidationCalls++;
        return [];
    }
}
