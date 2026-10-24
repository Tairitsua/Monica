using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Metrics;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Configuration.Services.Support;

public sealed class MonicaConfigurationProviderActivationCoordinatorTests
{
    [Fact]
    public void ShouldActivate_WhenManagedCatalogBecomesEmpty_ShouldPublishWithdrawalWhileStartupOnlyStaysIndependent()
    {
        var registry = new ConfigurationDefinitionRegistry();
        MonicaConfigurationProviderActivationCoordinator.ShouldActivate(registry, new StorePresenceProvider(false)).Should().BeFalse();
        MonicaConfigurationProviderActivationCoordinator.ShouldActivate(registry, new StorePresenceProvider(true)).Should().BeTrue();
        registry.Register(TestConfigurationFactory.Definition() with { Kind = ConfigurationKind.Startup });
        MonicaConfigurationProviderActivationCoordinator.ShouldActivate(registry, new StorePresenceProvider(true)).Should().BeFalse();
        registry.Register(TestConfigurationFactory.Definition());
        MonicaConfigurationProviderActivationCoordinator.ShouldActivate(registry, new StorePresenceProvider(true)).Should().BeTrue();
    }

    private sealed class StorePresenceProvider(bool configured) : IServiceProvider, IServiceProviderIsService
    {
        public object GetService(Type serviceType) => serviceType == typeof(IServiceProviderIsService)
            ? this : throw new InvalidOperationException("The activation decision must not resolve a store.");
        public bool IsService(Type serviceType) => configured && serviceType == typeof(IConfigurationMetadataStore);
    }

    [Fact]
    public async Task ActivateAsync_ShouldPublishBeforeExposingProviderOrStartingProjection_AndActivateOnce()
    {
        var publicationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProjection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = CreateFixture(
            async cancellationToken =>
            {
                publicationStarted.TrySetResult();
                await releasePublication.Task.WaitAsync(cancellationToken);
            },
            async cancellationToken =>
            {
                projectionStarted.TrySetResult();
                await releaseProjection.Task.WaitAsync(cancellationToken);
            });

        var activation = fixture.Coordinator.ActivateAsync(TestContext.Current.CancellationToken);
        await publicationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        activation.IsCompleted.Should().BeFalse();
        fixture.Accessor.ServiceProvider.Should().BeNull();
        fixture.ReloadCoordinator.InvocationCount.Should().Be(0);

        releasePublication.TrySetResult();
        await projectionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        activation.IsCompleted.Should().BeFalse();
        fixture.Accessor.ServiceProvider.Should().BeSameAs(fixture.ServiceProvider);
        fixture.ReloadCoordinator.InvocationCount.Should().Be(1);

        releaseProjection.TrySetResult();
        await activation;
        await fixture.Coordinator.ActivateAsync(TestContext.Current.CancellationToken);

        await fixture.MetadataStore.Received(1).PublishAsync(
            Arg.Any<ConfigurationDefinitionPublicationBatch>(),
            Arg.Any<CancellationToken>());
        fixture.ReloadCoordinator.InvocationCount.Should().Be(1);
        fixture.StateTracker.Operations.Should().Equal((METADATA_STORE_KEY, true, (string?)null));
    }

    [Fact]
    public async Task ActivateAsync_WhenPrePublicationDocumentWasDeleted_ShouldReactivateBeforeRecreatingDocument()
    {
        var definition = TestConfigurationFactory.Definition();
        var publisherStateIsActive = false;
        var effectiveDocumentExists = false;
        var operations = new List<string>();
        using var fixture = CreateFixture(
            _ =>
            {
                operations.Add("publish-active-state");
                publisherStateIsActive = true;
                return Task.CompletedTask;
            },
            _ =>
            {
                publisherStateIsActive.Should().BeTrue();
                operations.Add("ensure-effective-document");
                effectiveDocumentExists = true;
                return Task.CompletedTask;
            },
            definitions: [definition]);

        await fixture.Coordinator.ActivateAsync(TestContext.Current.CancellationToken);

        operations.Should().Equal("publish-active-state", "ensure-effective-document");
        publisherStateIsActive.Should().BeTrue();
        effectiveDocumentExists.Should().BeTrue();
        await fixture.MetadataStore.Received(1).PublishAsync(
            Arg.Is<ConfigurationDefinitionPublicationBatch>(batch =>
                batch.Publications.Count == 1
                && batch.Publications[0].Definition.DefinitionKey == definition.DefinitionKey),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateAsync_WhenPublicationFails_ShouldNotExposeOrReloadProviderAndRemainRetryable()
    {
        var publicationFailure = new InvalidOperationException("Publication failed.");
        var publicationAttempt = 0;
        using var fixture = CreateFixture(
            _ => Interlocked.Increment(ref publicationAttempt) == 1
                ? Task.FromException(publicationFailure)
                : Task.CompletedTask,
            _ => Task.CompletedTask);

        Func<Task> firstActivation = () => fixture.Coordinator.ActivateAsync(TestContext.Current.CancellationToken);
        (await firstActivation.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should().BeSameAs(publicationFailure);

        fixture.Accessor.ServiceProvider.Should().BeNull();
        fixture.ReloadCoordinator.InvocationCount.Should().Be(0);
        fixture.StateTracker.Operations.Should().Equal((METADATA_STORE_KEY, false, publicationFailure.Message));

        await fixture.Coordinator.ActivateAsync(TestContext.Current.CancellationToken);
        await fixture.Coordinator.ActivateAsync(TestContext.Current.CancellationToken);

        publicationAttempt.Should().Be(2);
        fixture.ReloadCoordinator.InvocationCount.Should().Be(1);
        fixture.Accessor.ServiceProvider.Should().BeSameAs(fixture.ServiceProvider);
        fixture.StateTracker.Operations.Should().Equal(
            (METADATA_STORE_KEY, false, publicationFailure.Message),
            (METADATA_STORE_KEY, true, (string?)null));
    }

    [Fact]
    public async Task ActivateAsync_WhenProjectionFails_ShouldNotMaskFailureWithMetadataSuccess()
    {
        var projectionFailure = new InvalidOperationException("Projection failed.");
        var projectionAttempt = 0;
        using var fixture = CreateFixture(
            _ => Task.CompletedTask,
            _ => Interlocked.Increment(ref projectionAttempt) == 1
                ? Task.FromException(projectionFailure)
                : Task.CompletedTask);

        Func<Task> firstActivation = () => fixture.Coordinator.ActivateAsync(TestContext.Current.CancellationToken);
        (await firstActivation.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should().BeSameAs(projectionFailure);

        fixture.StateTracker.Operations.Should().BeEmpty();

        await fixture.Coordinator.ActivateAsync(TestContext.Current.CancellationToken);

        await fixture.MetadataStore.Received(2).PublishAsync(
            Arg.Any<ConfigurationDefinitionPublicationBatch>(),
            Arg.Any<CancellationToken>());
        fixture.StateTracker.Operations.Should().Equal((METADATA_STORE_KEY, true, (string?)null));
    }

    [Fact]
    public async Task ActivateAsync_WhenIndependentMetadataStoreSucceeds_ShouldPublishItsHealthBeforeProjectionFailure()
    {
        using var fixture = CreateFixture(
            _ => Task.CompletedTask,
            _ => Task.FromException(new InvalidOperationException("Projection failed.")),
            effectiveStoreKey: "test-effective");

        Func<Task> activation = () => fixture.Coordinator.ActivateAsync(TestContext.Current.CancellationToken);

        await activation.Should().ThrowAsync<InvalidOperationException>();
        fixture.StateTracker.Operations.Should().Equal((METADATA_STORE_KEY, true, (string?)null));
    }

    [Fact]
    public async Task ActivateAsync_WhenPublicationIsCancelled_ShouldNotStartProjectionAndRemainRetryable()
    {
        var publicationAttempt = 0;
        var publicationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = CreateFixture(
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref publicationAttempt) > 1)
                {
                    return;
                }

                publicationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            _ => Task.CompletedTask);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var activation = fixture.Coordinator.ActivateAsync(cancellation.Token);
        await publicationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.Accessor.ServiceProvider.Should().BeNull();
        fixture.ReloadCoordinator.InvocationCount.Should().Be(0);
        await cancellation.CancelAsync();

        Func<Task> observeCancellation = () => activation;
        await observeCancellation.Should().ThrowAsync<OperationCanceledException>();
        await fixture.Coordinator.ActivateAsync(TestContext.Current.CancellationToken);

        publicationAttempt.Should().Be(2);
        fixture.ReloadCoordinator.InvocationCount.Should().Be(1);
        fixture.StateTracker.Operations.Should().HaveCount(2);
        fixture.StateTracker.Operations[0].StoreKey.Should().Be(METADATA_STORE_KEY);
        fixture.StateTracker.Operations[0].Succeeded.Should().BeFalse();
        fixture.StateTracker.Operations[0].Error.Should().NotBeNullOrWhiteSpace();
        fixture.StateTracker.Operations[1].Should().Be((METADATA_STORE_KEY, true, (string?)null));
    }

    [Fact]
    public void RecordStartupStageDuration_ShouldEmitOnlyBoundedStageAndResultDimensions()
    {
        var measurements = new ConcurrentBag<MetricMeasurement>();
        using var services = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider();
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meterFactory)
                    && instrument.Meter.Name == ConfigurationMetrics.MeterName
                    && instrument.Name == ConfigurationMetrics.StartupStageDuration)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            measurements.Add(new MetricMeasurement(value, tags.ToArray()));
        });
        listener.Start();
        var recorder = new ConfigurationMetricsRecorder(meterFactory);

        recorder.RecordStartupStageDuration(
            ConfigurationStartupStage.MetadataPublication,
            ConfigurationStartupResult.Success,
            TimeSpan.FromMilliseconds(12));
        recorder.RecordStartupStageDuration(
            ConfigurationStartupStage.ProjectionReload,
            ConfigurationStartupResult.Failure,
            TimeSpan.FromMilliseconds(34));
        recorder.RecordStartupStageDuration(
            ConfigurationStartupStage.ProviderActivation,
            ConfigurationStartupResult.Cancelled,
            TimeSpan.FromMilliseconds(56));
        recorder.RecordStartupStageDuration(
            ConfigurationStartupStage.RuntimeValidation,
            ConfigurationStartupResult.Success,
            TimeSpan.FromMilliseconds(78));

        measurements.Should().HaveCount(4);
        measurements.Should().Contain(measurement =>
            measurement.Value == 12
            && measurement.HasTag("stage", "metadata-publication")
            && measurement.HasTag("result", "success"));
        measurements.Should().Contain(measurement =>
            measurement.Value == 34
            && measurement.HasTag("stage", "projection-reload")
            && measurement.HasTag("result", "failure"));
        measurements.Should().Contain(measurement =>
            measurement.Value == 56
            && measurement.HasTag("stage", "provider-activation")
            && measurement.HasTag("result", "cancelled"));
        measurements.Should().Contain(measurement =>
            measurement.Value == 78
            && measurement.HasTag("stage", "runtime-validation")
            && measurement.HasTag("result", "success"));
        measurements.Should().OnlyContain(measurement => measurement.Tags.Count == 2);
    }

    private const string METADATA_STORE_KEY = "test-metadata";

    private static ActivationFixture CreateFixture(
        Func<CancellationToken, Task> publish,
        Func<CancellationToken, Task> reload,
        string effectiveStoreKey = METADATA_STORE_KEY,
        IReadOnlyList<ConfigurationDefinition>? definitions = null)
    {
        var services = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider();
        var accessor = new MonicaConfigurationProviderAccessor();
        var definitionRegistry = Substitute.For<IConfigurationDefinitionRegistry>();
        definitionRegistry.GetAll().Returns(definitions ?? []);
        var metadataStore = Substitute.For<IConfigurationMetadataStore>();
        metadataStore.Descriptor.Returns(new ConfigurationStoreDescriptor
        {
            StoreKey = METADATA_STORE_KEY,
            DisplayName = "Test metadata store",
            SupportsMetadata = true
        });
        metadataStore.PublishAsync(
                Arg.Any<ConfigurationDefinitionPublicationBatch>(),
                Arg.Any<CancellationToken>())
            .Returns(call => publish(call.ArgAt<CancellationToken>(1)));
        var effectiveValueStore = Substitute.For<IConfigurationEffectiveValueStore>();
        effectiveValueStore.Descriptor.Returns(new ConfigurationStoreDescriptor
        {
            StoreKey = effectiveStoreKey,
            DisplayName = "Test effective-value store",
            SupportsEffectiveValues = true
        });
        var reloadCoordinator = new RecordingReloadCoordinator(reload);
        var stateTracker = new RecordingStoreStateTracker();
        var hostEnvironment = Substitute.For<IHostEnvironment>();
        hostEnvironment.ApplicationName.Returns("Test.Monica.Configuration");
        var publisherIdentityProvider = new ConfigurationPublisherIdentityProvider(
            hostEnvironment,
            Options.Create(new ModuleConfigurationOption
            {
                PublisherKey = "test-publisher",
                InstanceId = "test-instance"
            }));
        var coordinator = new MonicaConfigurationProviderActivationCoordinator(
            services,
            accessor,
            definitionRegistry,
            metadataStore,
            effectiveValueStore,
            publisherIdentityProvider,
            new ConfigurationDefinitionResolver(definitionRegistry, metadataStore),
            stateTracker,
            reloadCoordinator,
            new ConfigurationMetricsRecorder(services.GetRequiredService<IMeterFactory>()));

        return new ActivationFixture(
            services,
            accessor,
            metadataStore,
            reloadCoordinator,
            stateTracker,
            coordinator);
    }

    private sealed record MetricMeasurement(
        double Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags)
    {
        public bool HasTag(string name, string value)
        {
            return Tags.Any(tag => tag.Key == name && Equals(tag.Value, value));
        }
    }

    private sealed class RecordingStoreStateTracker : IConfigurationStoreStateTracker
    {
        private readonly List<(string StoreKey, bool Succeeded, string? Error)> _operations = [];

        public IReadOnlyList<(string StoreKey, bool Succeeded, string? Error)> Operations => _operations;

        public void RecordSuccess(string storeKey)
        {
            _operations.Add((storeKey, true, null));
        }

        public void RecordFailure(string storeKey, Exception exception)
        {
            _operations.Add((storeKey, false, exception.Message));
        }

        public IReadOnlyList<ConfigurationStoreState> GetStates()
        {
            return [];
        }
    }

    private sealed class ActivationFixture(
        ServiceProvider serviceProvider,
        MonicaConfigurationProviderAccessor accessor,
        IConfigurationMetadataStore metadataStore,
        RecordingReloadCoordinator reloadCoordinator,
        RecordingStoreStateTracker stateTracker,
        MonicaConfigurationProviderActivationCoordinator coordinator)
        : IDisposable
    {
        public ServiceProvider ServiceProvider { get; } = serviceProvider;

        public MonicaConfigurationProviderAccessor Accessor { get; } = accessor;

        public IConfigurationMetadataStore MetadataStore { get; } = metadataStore;

        public RecordingReloadCoordinator ReloadCoordinator { get; } = reloadCoordinator;

        public RecordingStoreStateTracker StateTracker { get; } = stateTracker;

        public MonicaConfigurationProviderActivationCoordinator Coordinator { get; } = coordinator;

        public void Dispose()
        {
            ServiceProvider.Dispose();
        }
    }

    private sealed class RecordingReloadCoordinator(Func<CancellationToken, Task> reload)
        : IConfigurationReloadCoordinator
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public async Task ReloadMonicaProjectionAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            await reload(cancellationToken);
        }

        public Task ReloadMonicaProjectionAsync(
            string definitionKey,
            long? minimumVersion,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public long? GetLoadedMonicaProjectionVersion(string definitionKey)
        {
            throw new NotSupportedException();
        }

        public Task ReloadRuntimeConfigurationAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
