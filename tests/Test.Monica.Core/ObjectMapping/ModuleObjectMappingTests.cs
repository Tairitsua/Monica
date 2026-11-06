using AwesomeAssertions;
using Mapster;
using MapsterMapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Annotations;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Core.ObjectMapping.Abstractions;
using Monica.Core.ObjectMapping.Facades;
using Monica.Core.ObjectMapping.Providers.Mapster;
using Monica.Core.Results;
using Monica.Core.TypeDiscovery.Services;
using Monica.Modules;
using Xunit;
using TestContext = Xunit.TestContext;

namespace Test.Monica.Core.ObjectMapping;

[Collection(BlockingConcurrencyCollection.Name)]
public sealed class ModuleObjectMappingTests
{
    private static readonly TimeSpan HANG_GUARD = TimeSpan.FromSeconds(10);

    [Fact]
    public void Build_ShouldRegisterTheHostOwnedProfileCatalogAsSingleton()
    {
        using var host = BuildHost();

        var catalog = host.Services.GetRequiredService<MapsterProfileCatalog>();

        host.Services.GetRequiredService<MapsterProfileCatalog>().Should().BeSameAs(catalog);
        catalog.GetOrderedProfileTypes([], CreateTypeDependencyOrderer())
            .Should().Contain(typeof(InternalMappingProfile));
    }

    [Fact]
    public void Build_ShouldProvideObjectMappingFromTheHostOwnedRuntime()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var mapper = scope.ServiceProvider.GetRequiredService<IObjectMapper>();

        mapper.Map<MappingDestination>(new MappingSource { Value = "flight" })
            .Value.Should().Be("flight-profile");
    }

    [Fact]
    public void Build_WhenExplicitProfileOwnsPair_ShouldApplyAutomaticRefinementAfterIt()
    {
        using var host = BuildHost(mapping => mapping.AddProfile<ExplicitOwnerProfile>());
        using var scope = host.Services.CreateScope();

        var mapper = scope.ServiceProvider.GetRequiredService<IObjectMapper>();

        var destination = mapper.Map<ExplicitFirstDestination>(new ExplicitFirstSource());

        destination.ExplicitLayer.Should().Be("explicit");
        destination.AutomaticLayer.Should().Be("automatic");
    }

    [Fact]
    public void Catalog_WhenExplicitProfileIsAlsoDiscovered_ShouldKeepItOnceBeforeAutomaticProfiles()
    {
        var catalog = new MapsterProfileCatalog();
        catalog.Discover(typeof(InternalMappingProfile));
        catalog.Discover(typeof(AutomaticRefinementProfile));

        var profiles = catalog.GetOrderedProfileTypes(
            [typeof(InternalMappingProfile)],
            CreateTypeDependencyOrderer());

        profiles.Should().Equal(typeof(InternalMappingProfile), typeof(AutomaticRefinementProfile));
    }

    [Fact]
    public void Catalog_WhenProfilesCannotBeActivated_ShouldIgnoreAbstractAndOpenGenericTypes()
    {
        var catalog = new MapsterProfileCatalog();

        catalog.Discover(typeof(AbstractMappingProfile));
        catalog.Discover(typeof(OpenGenericMappingProfile<>));

        catalog.GetOrderedProfileTypes([], CreateTypeDependencyOrderer())
            .Should().BeEmpty();
    }

    [Fact]
    public void Build_WhenProfileIsRegisteredTwice_ShouldApplyItOnce()
    {
        CountingMappingProfile.Reset();

        using var host = BuildHost(mapping => mapping
            .AddProfile<CountingMappingProfile>()
            .AddProfile<CountingMappingProfile>());
        using var scope = host.Services.CreateScope();

        var mapper = scope.ServiceProvider.GetRequiredService<IObjectMapper>();

        mapper.Map<CountingDestination>(new CountingSource())
            .RegistrationCount.Should().Be(1);
        CountingMappingProfile.RegistrationCount.Should().Be(1);
    }

    [Fact]
    public void Build_WhenProfilesRefineOnePair_ShouldUseRegistrationOrder()
    {
        RefinementRegistrationTrace.Reset();

        using var host = BuildHost(mapping => mapping
            .AddProfile<PlatformRefinementProfile>()
            .AddProfile<DomainRefinementProfile>()
            .AddProfile<AdapterRefinementProfile>());
        using var scope = host.Services.CreateScope();

        var mapper = scope.ServiceProvider.GetRequiredService<IObjectMapper>();

        var destination = mapper.Map<RefinementDestination>(new RefinementSource());

        destination.PlatformLayer.Should().Be("platform");
        destination.DomainLayer.Should().Be("domain");
        destination.AdapterLayer.Should().Be("adapter");
        RefinementRegistrationTrace.Snapshot().Should().Equal("platform", "domain", "adapter");
    }

    [Fact]
    public async Task StartAsync_WithStrictCompilationBarrier_ShouldReportEveryCompilationError()
    {
        using var host = BuildHost(
            mapping => mapping.AddProfile<InvalidMappingProfile>(),
            options =>
            {
                options.CompilationBarrier = ModuleStartupWorkBarrier.BeforeHostLifecycle;
                options.CompileFailFast = false;
            });

        Func<Task> start = () => host.StartAsync(TestContext.Current.CancellationToken);

        await start.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage($"*{nameof(InvalidDestinationOne)}*")
            .WithMessage($"*{nameof(InvalidDestinationTwo)}*");
    }

    [Fact]
    public async Task Build_WithDefaultCompilationBarrier_ShouldServeConcurrentLazyMappingsAndPublishAtomically()
    {
        using var compilation = new CompilationProbe();
        BlockingCompilerProfile.SetProbe(compilation);
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddObjectMapping().AddProfile<BlockingCompilerProfile>();
        });
        IHost? host = null;

        try
        {
            compilation.WaitUntilEntered(HANG_GUARD, TestContext.Current.CancellationToken);
            host = builder.Build();
            var application = host.Services.GetRequiredService<global::Monica.Core.MonicaApplication>();
            var runtime = host.Services.GetRequiredService<MapsterConfigurationRuntime>();
            using var scope = host.Services.CreateScope();
            var mapper = scope.ServiceProvider.GetRequiredService<IObjectMapper>();
            var mapsterMapper = mapper.Should().BeOfType<MapsterObjectMapper>().Which;

            runtime.HasPublishedCompiledConfiguration.Should().BeFalse();
            var fallbackMapper = mapsterMapper.GetCurrentMapper();
            mapsterMapper.GetCurrentMapper().Should().BeSameAs(fallbackMapper);
            var lazyMappings = Enumerable.Range(0, 32)
                .Select(index => Task.Run(
                    () => mapper.Map<ConcurrentCompilationDestination>(
                            new ConcurrentCompilationSource { Value = $"lazy-{index}" })
                        .Value,
                    TestContext.Current.CancellationToken));
            var lazyResults = await Task.WhenAll(lazyMappings);

            lazyResults.Should().Equal(Enumerable.Range(0, 32).Select(index => $"lazy-{index}"));
            runtime.HasPublishedCompiledConfiguration.Should().BeFalse();

            compilation.Release();
            application.Modules.DrainStartupWork();
            runtime.HasPublishedCompiledConfiguration.Should().BeTrue();

            var publishedMappers = await Task.WhenAll(Enumerable.Range(0, 32)
                .Select(_ => Task.Run(
                    mapsterMapper.GetCurrentMapper,
                    TestContext.Current.CancellationToken)));
            publishedMappers.Should().AllSatisfy(publishedMapper =>
            {
                publishedMapper.Should().NotBeSameAs(fallbackMapper);
                publishedMapper.Should().BeSameAs(publishedMappers[0]);
                publishedMapper.Config.Should().BeSameAs(runtime.CurrentConfiguration);
            });
            mapper.Map<ConcurrentCompilationDestination>(new ConcurrentCompilationSource { Value = "compiled" })
                .Value.Should().Be("compiled");
        }
        finally
        {
            compilation.Release();
            host?.Dispose();
            BlockingCompilerProfile.ClearProbe();
        }
    }

    [Fact]
    public async Task StartAsync_WhenNonBlockingCompilationFails_ShouldKeepLazyMappingsAvailable()
    {
        using var host = BuildHost(
            mapping => mapping.AddProfile<InvalidMappingProfile>(),
            options => options.CompileFailFast = false);
        var application = host.Services.GetRequiredService<global::Monica.Core.MonicaApplication>();

        application.Modules.DrainStartupWork();
        GetCompilationWork(application)!.Status.Should().Be(ModuleStartupWorkStatus.Failed);

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var scope = host.Services.CreateScope();
            var mapper = scope.ServiceProvider.GetRequiredService<IObjectMapper>();

            mapper.Map<MappingDestination>(new MappingSource { Value = "fallback" })
                .Value.Should().Be("fallback-profile");
            Action invalidMap = () => mapper.Map<InvalidSourceOne, InvalidDestinationOne>(new InvalidSourceOne());
            invalidMap.Should().Throw<Exception>()
                .Which.ToString().Should().Contain(nameof(InvalidDestinationOne));

            GetCompilationWork(application)!.ErrorMessage.Should()
                .Contain(nameof(InvalidDestinationOne))
                .And.Contain(nameof(InvalidDestinationTwo));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void Build_ShouldNotExposeRawMapsterRuntimeServices()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        host.Services.GetService<TypeAdapterConfig>().Should().BeNull();
        scope.ServiceProvider.GetService<IMapper>().Should().BeNull();
        scope.ServiceProvider.GetRequiredService<IObjectMapper>().Should().BeOfType<MapsterObjectMapper>();
    }

    [Fact]
    public void Map_WithDependencyInjectedMapping_ShouldUseTheCurrentScope()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScoped(_ => new MappingSuffix("-from-scope"));
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddObjectMapping().AddProfile<DependencyInjectedMappingProfile>();
        });
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        var mapper = scope.ServiceProvider.GetRequiredService<IObjectMapper>();

        mapper.Map<DependencyInjectedDestination>(new DependencyInjectedSource { Value = "mapped" })
            .Value.Should().Be("mapped-from-scope");
    }

    [Fact]
    public void ProjectToType_ShouldUseThePublishedRuntimeConfiguration()
    {
        using var host = BuildHost();
        var application = host.Services.GetRequiredService<global::Monica.Core.MonicaApplication>();
        var runtime = host.Services.GetRequiredService<MapsterConfigurationRuntime>();
        application.Modules.DrainStartupWork();
        runtime.HasPublishedCompiledConfiguration.Should().BeTrue();
        using var scope = host.Services.CreateScope();
        var mapper = scope.ServiceProvider.GetRequiredService<IObjectMapper>();

        var projected = mapper.ProjectToType<MappingDestination>(
                new[] { new MappingSource { Value = "projected" } }.AsQueryable())
            .Single();

        projected.Value.Should().Be("projected-profile");
    }

    [Fact]
    public void Configure_WithCompositionPhaseCompilationBarrier_ShouldFailClearly()
    {
        Action compose = () => BuildHost(
            configureOption: options =>
            {
                options.CompilationBarrier = ModuleStartupWorkBarrier.BeforeServiceRegistrationCompletion;
            });

        compose.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{nameof(ModuleObjectMappingOption.CompilationBarrier)} only supports*")
            .WithMessage($"*{ModuleStartupWorkBarrier.NoBarrier}*")
            .WithMessage($"*{ModuleStartupWorkBarrier.BeforeHostLifecycle}*");
    }

    [Fact]
    public void AddMonica_WhenProfileConstructorThrows_ShouldReportModuleCompositionFailure()
    {
        Action compose = () => BuildHost(mapping => mapping.AddProfile<ThrowingConstructorProfile>());

        var exception = compose.Should().Throw<InvalidOperationException>().Which;
        exception.ToString().Should().Contain("constructor-profile-failure");
    }

    [Fact]
    public void AddMonica_WhenProfileRegisterThrows_ShouldReportModuleCompositionFailure()
    {
        Action compose = () => BuildHost(mapping => mapping.AddProfile<ThrowingRegisterProfile>());

        compose.Should().Throw<InvalidOperationException>()
            .WithMessage("*register-profile-failure*");
    }

    [Fact]
    public async Task Build_TwoHostsWithConflictingProfiles_ShouldRemainIsolatedAndLeaveGlobalSettingsUntouched()
    {
        GlobalSettingsContains<HostMappingSource, HostMappingDestination>().Should().BeFalse();

        var firstHostTask = Task.Run(
            () => BuildHost(mapping => mapping.AddProfile<FirstHostMappingProfile>()),
            TestContext.Current.CancellationToken);
        var secondHostTask = Task.Run(
            () => BuildHost(mapping => mapping.AddProfile<SecondHostMappingProfile>()),
            TestContext.Current.CancellationToken);
        var hosts = await Task.WhenAll(firstHostTask, secondHostTask);

        try
        {
            using var firstScope = hosts[0].Services.CreateScope();
            using var secondScope = hosts[1].Services.CreateScope();
            var firstMapper = firstScope.ServiceProvider.GetRequiredService<IObjectMapper>();
            var secondMapper = secondScope.ServiceProvider.GetRequiredService<IObjectMapper>();
            var firstRuntime = hosts[0].Services.GetRequiredService<MapsterConfigurationRuntime>();
            var secondRuntime = hosts[1].Services.GetRequiredService<MapsterConfigurationRuntime>();

            firstMapper.Map<HostMappingDestination>(new HostMappingSource()).Value.Should().Be("first-host");
            secondMapper.Map<HostMappingDestination>(new HostMappingSource()).Value.Should().Be("second-host");
            firstRuntime.CurrentConfiguration.Should().NotBeSameAs(secondRuntime.CurrentConfiguration);
            GlobalSettingsContains<HostMappingSource, HostMappingDestination>().Should().BeFalse();
        }
        finally
        {
            foreach (var host in hosts)
            {
                host.Dispose();
            }
        }
    }

    [Fact]
    public async Task Diagnostics_WhenMappingsAreInspected_ShouldNotMutateLiveConfiguration()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();
        var runtime = host.Services.GetRequiredService<MapsterConfigurationRuntime>();
        var config = runtime.CurrentConfiguration;
        var facade = scope.ServiceProvider.GetRequiredService<ObjectMappingFacade>();

        config.SelfContainedCodeGeneration.Should().BeFalse();

        var result = await facade.GetStatusAsync();

        result.Status.Should().Be(ResultStatus.Ok);
        result.Data.Should().NotBeNull();
        result.Data!.Mappings.Should().ContainSingle(mapping =>
            mapping.SourceType.Contains(nameof(MappingSource), StringComparison.Ordinal) &&
            mapping.DestinationType.Contains(nameof(MappingDestination), StringComparison.Ordinal));
        config.SelfContainedCodeGeneration.Should().BeFalse();
    }

    private static IHost BuildHost(
        Action<ModuleRegistration<ModuleObjectMapping, ModuleObjectMappingOption>>? configureMapping = null,
        Action<ModuleObjectMappingOption>? configureOption = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options
                .ExcludeDefault()
                .Add(typeof(ModuleObjectMappingTests).Assembly));
            var mapping = monica.AddObjectMapping(configureOption);
            configureMapping?.Invoke(mapping);
        });
        return builder.Build();
    }

    private static ModuleStartupWorkPerformanceInfo? GetCompilationWork(
        global::Monica.Core.MonicaApplication application)
    {
        return application.Profiling.GetCompositionPerformance().StartupWorkItems.SingleOrDefault(work =>
            work.ModuleTypeName == nameof(ModuleObjectMapping)
            && work.Name == "compile-mapster-configuration");
    }

    private static bool GlobalSettingsContains<TSource, TDestination>()
    {
        return TypeAdapterConfig.GlobalSettings.RuleMap.Keys.Any(key =>
            key.Source == typeof(TSource) && key.Destination == typeof(TDestination));
    }

    private static TypeDependencyOrderer CreateTypeDependencyOrderer()
    {
        return new TypeDependencyOrderer([typeof(ModuleObjectMappingTests).Assembly]);
    }

    private sealed class InternalMappingProfile : IRegister
    {
        private InternalMappingProfile()
        {
        }

        public void Register(TypeAdapterConfig config)
        {
            config.NewConfig<MappingSource, MappingDestination>()
                .Map(destination => destination.Value, source => $"{source.Value}-profile");
        }
    }

    private sealed class AutomaticRefinementProfile : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            config.ForType<ExplicitFirstSource, ExplicitFirstDestination>()
                .Map(destination => destination.AutomaticLayer, _ => "automatic");
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class ExplicitOwnerProfile : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            config.NewConfig<ExplicitFirstSource, ExplicitFirstDestination>()
                .Map(destination => destination.ExplicitLayer, _ => "explicit");
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class CountingMappingProfile : IRegister
    {
        private static int _registrationCount;

        public static int RegistrationCount => Volatile.Read(ref _registrationCount);

        public static void Reset()
        {
            Interlocked.Exchange(ref _registrationCount, 0);
        }

        public void Register(TypeAdapterConfig config)
        {
            var registrationCount = Interlocked.Increment(ref _registrationCount);
            config.NewConfig<CountingSource, CountingDestination>()
                .Map(destination => destination.RegistrationCount, _ => registrationCount);
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class PlatformRefinementProfile : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            RefinementRegistrationTrace.Record("platform");
            config.NewConfig<RefinementSource, RefinementDestination>()
                .Map(destination => destination.PlatformLayer, _ => "platform");
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class DomainRefinementProfile : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            RefinementRegistrationTrace.Record("domain");
            config.ForType<RefinementSource, RefinementDestination>()
                .Map(destination => destination.DomainLayer, _ => "domain");
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class AdapterRefinementProfile : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            RefinementRegistrationTrace.Record("adapter");
            config.ForType<RefinementSource, RefinementDestination>()
                .Map(destination => destination.AdapterLayer, _ => "adapter");
        }
    }

    private static class RefinementRegistrationTrace
    {
        private static readonly Lock _sync = new();
        private static readonly List<string> _registrations = [];

        public static void Reset()
        {
            lock (_sync)
            {
                _registrations.Clear();
            }
        }

        public static void Record(string profile)
        {
            lock (_sync)
            {
                _registrations.Add(profile);
            }
        }

        public static IReadOnlyList<string> Snapshot()
        {
            lock (_sync)
            {
                return [.. _registrations];
            }
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class InvalidMappingProfile : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            config.RequireExplicitMapping = true;
            config.NewConfig<InvalidSourceOne, InvalidDestinationOne>();
            config.NewConfig<InvalidSourceTwo, InvalidDestinationTwo>();
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class ThrowingConstructorProfile : IRegister
    {
        private ThrowingConstructorProfile()
        {
            throw new InvalidOperationException("constructor-profile-failure");
        }

        public void Register(TypeAdapterConfig config)
        {
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class ThrowingRegisterProfile : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            throw new InvalidOperationException("register-profile-failure");
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class BlockingCompilerProfile : IRegister
    {
        private static readonly AsyncLocal<CompilationProbe?> _probe = new();

        public static void SetProbe(CompilationProbe probe)
        {
            _probe.Value = probe;
        }

        public static void ClearProbe()
        {
            _probe.Value = null;
        }

        public void Register(TypeAdapterConfig config)
        {
            var probe = _probe.Value
                ?? throw new InvalidOperationException("The blocking compiler profile requires a test-owned probe.");
            var compilerEntered = 0;
            config.Compiler = expression =>
            {
                if (Interlocked.Exchange(ref compilerEntered, 1) == 0)
                {
                    probe.EnterCompiler();
                }

                return expression.Compile();
            };
            config.NewConfig<ConcurrentCompilationSource, ConcurrentCompilationDestination>();
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class DependencyInjectedMappingProfile : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            config.NewConfig<DependencyInjectedSource, DependencyInjectedDestination>()
                .Map(
                    destination => destination.Value,
                    source => MapContext.Current.GetService<MappingSuffix>().Apply(source.Value));
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class FirstHostMappingProfile : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            config.NewConfig<HostMappingSource, HostMappingDestination>()
                .Map(destination => destination.Value, _ => "first-host");
        }
    }

    [ExcludeFromBusinessTypeDiscovery]
    private sealed class SecondHostMappingProfile : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            config.NewConfig<HostMappingSource, HostMappingDestination>()
                .Map(destination => destination.Value, _ => "second-host");
        }
    }

    private sealed class MappingSource
    {
        public string Value { get; init; } = string.Empty;
    }

    private sealed class MappingDestination
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class ExplicitFirstSource;

    private sealed class ExplicitFirstDestination
    {
        public string ExplicitLayer { get; set; } = string.Empty;

        public string AutomaticLayer { get; set; } = string.Empty;
    }

    private sealed class CountingSource;

    private sealed class CountingDestination
    {
        public int RegistrationCount { get; set; }
    }

    private sealed class RefinementSource;

    private sealed class RefinementDestination
    {
        public string PlatformLayer { get; set; } = string.Empty;
        public string DomainLayer { get; set; } = string.Empty;
        public string AdapterLayer { get; set; } = string.Empty;
    }

    private sealed class InvalidSourceOne
    {
        public InvalidNestedSourceOne Nested { get; set; } = new();
    }

    private sealed class InvalidDestinationOne
    {
        public InvalidNestedDestinationOne Nested { get; set; } = new();
    }

    private sealed class InvalidNestedSourceOne;

    private sealed class InvalidNestedDestinationOne;

    private sealed class InvalidSourceTwo
    {
        public InvalidNestedSourceTwo Nested { get; set; } = new();
    }

    private sealed class InvalidDestinationTwo
    {
        public InvalidNestedDestinationTwo Nested { get; set; } = new();
    }

    private sealed class InvalidNestedSourceTwo;

    private sealed class InvalidNestedDestinationTwo;

    private sealed class HostMappingSource;

    private sealed class HostMappingDestination
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class ConcurrentCompilationSource
    {
        public string Value { get; init; } = string.Empty;
    }

    private sealed class ConcurrentCompilationDestination
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class DependencyInjectedSource
    {
        public string Value { get; init; } = string.Empty;
    }

    private sealed class DependencyInjectedDestination
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class MappingSuffix(string suffix)
    {
        public string Apply(string value)
        {
            return value + suffix;
        }
    }

    private sealed class CompilationProbe : IDisposable
    {
        private readonly ManualResetEventSlim _compilerEntered = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public void WaitUntilEntered(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (!_compilerEntered.Wait(timeout, cancellationToken))
            {
                throw new TimeoutException("The object-mapping compiler did not start.");
            }
        }

        public void EnterCompiler()
        {
            _compilerEntered.Set();
            _release.Wait();
        }

        public void Release()
        {
            _release.Set();
        }

        public void Dispose()
        {
            _release.Set();
            _compilerEntered.Dispose();
            _release.Dispose();
        }
    }

    private abstract class AbstractMappingProfile : IRegister
    {
        public abstract void Register(TypeAdapterConfig config);
    }

    private sealed class OpenGenericMappingProfile<T> : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
        }
    }

}
