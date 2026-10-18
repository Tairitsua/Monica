using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.EventBus.Services;
using Monica.EventBus.Services.Support;
using Monica.Modules;
using Xunit;

namespace Test.Monica.EventBus.Services;

public sealed class EventBusAutoDiscoveryLifecycleTests
{
    [Fact]
    public async Task InactiveExternalRole_PreservesSameTypesLocalSubscription()
    {
        var state = new DiscoveryActivationState();
        var builder = CreateBuilder();
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton<ITypeActivationPolicy, InactiveExternalRole>();
        builder.Services.AddScoped<DualScopeRoleHandler>();
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        var subscriptions = host.Services.GetRequiredService<IEventSubscriptionRegistry>().GetAll()
            .Where(subscription => subscription.HandlerType == typeof(DualScopeRoleHandler)).ToArray();
        Assert.Single(subscriptions);
        Assert.Equal(EventSubscriptionScope.Local, subscriptions[0].Scope);
        Assert.Equal(0, state.Constructions);
        await PublishAsync(host, new RoleScopedEvent());
        Assert.Equal(1, state.Deliveries);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class InactiveExternalRole : ITypeActivationPolicy
    {
        public bool IsEnabled(Type implementationType, TypeActivationKind kind) => kind != TypeActivationKind.ExternalSubscription;
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType) => null;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task StartingAsync_WhenDiscoveryIsDisabledOrEmpty_ShouldNotRequireRegistrationInspector(
        bool disableAutoDiscovery, bool discoverHandlers)
    {
        var builder = CreateBuilder(disableAutoDiscovery: disableAutoDiscovery, discoverHandlers: discoverHandlers);
        RemoveRegistrationInspectorFromLifecycle(builder);
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<IEventSubscriptionRegistry>().GetAll().Should().BeEmpty();
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartingAsync_WhenAutomaticHandlersHaveNoRegistrationInspector_ShouldFailWithoutConstructingHandlers()
    {
        var state = new DiscoveryActivationState();
        var builder = CreateBuilder();
        builder.Services.AddSingleton(state);
        builder.Services.AddScoped<LazyDiscoveryHandler>();
        RemoveRegistrationInspectorFromLifecycle(builder);
        using var host = builder.Build();

        Func<Task> start = () => host.StartAsync(TestContext.Current.CancellationToken);
        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("EventBus automatic discovery requires IServiceProviderIsService registration metadata from the host container.");

        host.Services.GetRequiredService<IEventSubscriptionRegistry>().GetAll().Should().BeEmpty();
        state.Constructions.Should().Be(0);
    }

    [Fact]
    public async Task StartingAsync_WhenHandlerIsRegistered_ShouldDeferConstructionToEachDelivery()
    {
        var state = new DiscoveryActivationState();
        var builder = CreateBuilder();
        builder.Services.AddSingleton(state);
        builder.Services.AddScoped<LazyDiscoveryHandler>();
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var registry = host.Services.GetRequiredService<IEventSubscriptionRegistry>();
        registry.GetAll().Count(subscription => subscription.HandlerType == typeof(LazyDiscoveryHandler))
            .Should().Be(2);
        state.Constructions.Should().Be(0);

        await PublishAsync(host, new DiscoveryProbeEvent());
        await PublishAsync(host, new SecondaryDiscoveryProbeEvent());

        state.Constructions.Should().Be(2);
        state.Deliveries.Should().Be(2);
        state.HandlerDisposals.Should().Be(2);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartingAsync_WhenHandlerIsUnregistered_ShouldExcludeItsSubscription()
    {
        using var host = CreateBuilder().Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var registry = host.Services.GetRequiredService<IEventSubscriptionRegistry>();
        registry.GetAll().Should().HaveCount(2);
        registry.GetAll().Should().NotContain(subscription =>
            subscription.HandlerType == typeof(UnregisteredDiscoveryHandler));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartingAsync_WhenRegisteredConstructorThrows_ShouldKeepSubscriptionAndFailOnlyOnDelivery()
    {
        var state = new DiscoveryActivationState();
        var builder = CreateBuilder();
        builder.Services.AddSingleton(state);
        builder.Services.AddScoped<ThrowingDiscoveryHandler>();
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var registry = host.Services.GetRequiredService<IEventSubscriptionRegistry>();
        registry.GetAll().Should().ContainSingle(subscription =>
            subscription.HandlerType == typeof(ThrowingDiscoveryHandler) && subscription.IsAutoDiscovered);
        state.Constructions.Should().Be(0);

        Func<Task> publish = () => PublishAsync(host, new ThrowingDiscoveryEvent());
        await publish.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Handler construction failed.");

        state.Constructions.Should().Be(1);
        state.Deliveries.Should().Be(0);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartingAsync_WhenHandlerOrDependencyRequiresAsyncDisposal_ShouldActivateAndDisposeOnlyDuringDelivery(
        bool hasAsyncOnlyDependency)
    {
        var state = new DiscoveryActivationState();
        var builder = CreateBuilder();
        builder.Services.AddSingleton(state);
        var handlerType = hasAsyncOnlyDependency
            ? typeof(AsyncDependentDiscoveryHandler)
            : typeof(AsyncOnlyDiscoveryHandler);
        builder.Services.AddScoped(handlerType);
        if (hasAsyncOnlyDependency) builder.Services.AddScoped<AsyncOnlyDiscoveryDependency>();
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var registry = host.Services.GetRequiredService<IEventSubscriptionRegistry>();
        registry.GetAll().Should().ContainSingle(subscription =>
            subscription.HandlerType == handlerType && subscription.IsAutoDiscovered);
        state.Constructions.Should().Be(0);
        state.DependencyConstructions.Should().Be(0);
        state.HandlerDisposals.Should().Be(0);
        state.DependencyDisposals.Should().Be(0);

        await PublishAsync(host, new DiscoveryProbeEvent());
        await PublishAsync(host, new DiscoveryProbeEvent());

        state.Constructions.Should().Be(2);
        state.Deliveries.Should().Be(2);
        state.HandlerDisposals.Should().Be(hasAsyncOnlyDependency ? 0 : 2);
        state.DependencyConstructions.Should().Be(hasAsyncOnlyDependency ? 2 : 0);
        state.DependencyDisposals.Should().Be(hasAsyncOnlyDependency ? 2 : 0);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HostLifecycle_ShouldActivateBeforeProviderAndRemoveOnlyOwnedSubscriptionsBeforeProviderStops()
    {
        var builder = CreateBuilder();
        builder.Services.AddSingleton<SubscriptionProviderProbe>();
        builder.Services.AddHostedService(
            serviceProvider => serviceProvider.GetRequiredService<SubscriptionProviderProbe>());

        using var host = builder.Build();
        var registry = host.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var changes = new SubscriptionChangeRecorder();
        using var observation = registry.Subscribe(changes);
        var manual = await AddManualSubscriptionAsync(host, registry);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var ownedCreationOrder = changes.Changes
            .Where(change => change is { ChangeType: EventSubscriptionChangeType.Added, Subscription.IsAutoDiscovered: true })
            .Select(change => change.Subscription.Id)
            .ToArray();
        ownedCreationOrder.Should().HaveCount(2);
        registry.GetAll().Should().HaveCount(ownedCreationOrder.Length + 1);

        await host.StopAsync(TestContext.Current.CancellationToken);

        var probe = host.Services.GetRequiredService<SubscriptionProviderProbe>();
        probe.SubscriptionCountAtStart.Should().Be(ownedCreationOrder.Length + 1);
        probe.SubscriptionCountAtStop.Should().Be(1);
        registry.GetAll().Should().ContainSingle(subscription => subscription.Id == manual.Id);
        changes.Changes
            .Where(change => change is { ChangeType: EventSubscriptionChangeType.Removed, Subscription.IsAutoDiscovered: true })
            .Select(change => change.Subscription.Id)
            .Should().Equal(ownedCreationOrder.Reverse());

        await registry.UnsubscribeAsync(manual.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dispose_WhenLaterHostedServiceFailsStartup_ShouldRemoveOwnedSubscriptions()
    {
        var builder = CreateBuilder();
        builder.Services.AddHostedService<FailingStartService>();
        var host = builder.Build();
        var registry = host.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var manual = await AddManualSubscriptionAsync(host, registry);

        Func<Task> start = () => host.StartAsync(TestContext.Current.CancellationToken);
        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Provider startup failed.");
        registry.GetAll().Count(subscription => subscription.IsAutoDiscovered).Should().Be(2);

        host.Dispose();

        registry.GetAll().Should().ContainSingle(subscription => subscription.Id == manual.Id);
        await registry.UnsubscribeAsync(manual.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartingAsync_WhenCancelledAfterPartialCreation_ShouldRollBackOwnedSubscriptionsAndPreserveManualSubscription()
    {
        var registry = new ControlledBatchRegistry(blockAfterFirstSubscription: true);
        var builder = CreateBuilder(registry);
        using var host = builder.Build();
        var manual = await AddManualSubscriptionAsync(host, registry);
        using var cancellation = new CancellationTokenSource();

        var startTask = host.StartAsync(cancellation.Token);
        await registry.BatchEntered.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        Func<Task> observeStart = () => startTask;
        await observeStart.Should().ThrowAsync<OperationCanceledException>();
        registry.GetAll().Should().ContainSingle(subscription => subscription.Id == manual.Id);
        registry.RollbackOrder.Should().ContainSingle();

        await registry.UnsubscribeAsync(manual.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartingAndStoppingAsync_WhenConcurrent_ShouldSerializePhasesAndPreserveManualSubscription()
    {
        var registry = new ControlledBatchRegistry(blockAfterFirstSubscription: true);
        var builder = CreateBuilder(registry);
        using var host = builder.Build();
        var manual = await AddManualSubscriptionAsync(host, registry);
        var lifecycle = host.Services.GetServices<IHostedService>()
            .OfType<IHostedLifecycleService>()
            .Single(service => service.GetType().Name == "EventBusAutoDiscoveryLifecycle");

        var startingTask = lifecycle.StartingAsync(TestContext.Current.CancellationToken);
        await registry.BatchEntered.WaitAsync(TestContext.Current.CancellationToken);

        var stoppingTask = lifecycle.StoppingAsync(TestContext.Current.CancellationToken);
        stoppingTask.IsCompleted.Should().BeFalse();

        registry.ReleaseBatch();
        await startingTask;
        await stoppingTask;

        registry.GetAll().Should().ContainSingle(subscription => subscription.Id == manual.Id);
        registry.UnsubscribeOrder
            .Where(subscriptionId => subscriptionId != manual.Id)
            .Should().Equal(registry.OwnedCreationOrder.Reverse());

        await registry.UnsubscribeAsync(manual.Id, TestContext.Current.CancellationToken);
    }

    private static async Task PublishAsync<TEvent>(IHost host, TEvent message) where TEvent : class
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILocalEventBus>()
            .PublishAsync(message, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static void RemoveRegistrationInspectorFromLifecycle(HostApplicationBuilder builder)
    {
        var registration = builder.Services.Single(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType?.Name == "EventBusAutoDiscoveryLifecycle");
        var factory = ActivatorUtilities.CreateFactory(registration.ImplementationType!, [typeof(IServiceProviderIsService)]);
        builder.Services.Remove(registration);
        // Supply an absent inspector explicitly while retaining the real host, module and lifecycle.
        builder.Services.AddSingleton<IHostedService>(provider => (IHostedService)factory(provider, [null]));
    }

    private static HostApplicationBuilder CreateBuilder(IEventSubscriptionRegistry? registry = null,
        bool disableAutoDiscovery = false, bool discoverHandlers = true)
    {
        var builder = Host.CreateApplicationBuilder();
        // Auto-discovery only subscribes handler types this host registered; dispatch resolves them from DI.
        builder.Services.AddTransient<AutoDiscoveredHandler>();
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ServicesStartConcurrently = true;
            options.ServicesStopConcurrently = true;
        });
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options =>
            {
                options.ExcludeDefault();
                if (discoverHandlers) options.Add(typeof(EventBusAutoDiscoveryLifecycleTests).Assembly);
            });
            monica.AddEventBus(options => options.DisableAutoDiscovery = disableAutoDiscovery);
        });

        if (registry is not null)
        {
            builder.Services.Replace(ServiceDescriptor.Singleton(registry));
        }

        return builder;
    }

    private static Task<IEventSubscription> AddManualSubscriptionAsync(
        IHost host,
        IEventSubscriptionRegistry registry)
    {
        return registry.SubscribeAsync(
            new EventSubscriptionDescriptor
            {
                EventType = typeof(LifecycleEvent),
                TopicName = "manual",
                HandlerFactory = new IocEventHandlerFactory(
                    host.Services.GetRequiredService<IServiceScopeFactory>(),
                    typeof(AutoDiscoveredHandler)),
                Scope = EventSubscriptionScope.Local,
                IsAutoDiscovered = false
            },
            TestContext.Current.CancellationToken);
    }
}

public sealed record LifecycleEvent;

public sealed record RoleScopedEvent;
public sealed record RoleExternalEvent;
public sealed class DualScopeRoleHandler : ILocalEventHandler<RoleScopedEvent>, IDistributedEventHandler<RoleExternalEvent>
{
    private readonly DiscoveryActivationState _state;
    public DualScopeRoleHandler(DiscoveryActivationState state)
    {
        _state = state;
        state.Constructions++;
    }
    public Task HandleEventAsync(RoleScopedEvent eventData, CancellationToken cancellationToken)
    {
        _state.Deliveries++;
        return Task.CompletedTask;
    }
    public Task HandleEventAsync(RoleExternalEvent eventData, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The inactive external role must not receive this event.");
}

public sealed record SecondaryLifecycleEvent;

public sealed record DiscoveryProbeEvent;

public sealed record SecondaryDiscoveryProbeEvent;

public sealed record UnregisteredDiscoveryEvent;

public sealed record ThrowingDiscoveryEvent;

public sealed class DiscoveryActivationState
{
    public int Constructions { get; set; }
    public int Deliveries { get; set; }
    public int HandlerDisposals { get; set; }
    public int DependencyConstructions { get; set; }
    public int DependencyDisposals { get; set; }
}

public sealed class LazyDiscoveryHandler :
    ILocalEventHandler<DiscoveryProbeEvent>,
    ILocalEventHandler<SecondaryDiscoveryProbeEvent>,
    IDisposable
{
    private readonly DiscoveryActivationState _state;

    public LazyDiscoveryHandler(DiscoveryActivationState state)
    {
        _state = state;
        _state.Constructions++;
    }

    public Task HandleEventAsync(DiscoveryProbeEvent eventData, CancellationToken cancellationToken)
    {
        _state.Deliveries++;
        return Task.CompletedTask;
    }

    public Task HandleEventAsync(SecondaryDiscoveryProbeEvent eventData, CancellationToken cancellationToken)
    {
        _state.Deliveries++;
        return Task.CompletedTask;
    }

    public void Dispose() => _state.HandlerDisposals++;
}

public sealed class UnregisteredDiscoveryHandler : ILocalEventHandler<UnregisteredDiscoveryEvent>
{
    public Task HandleEventAsync(UnregisteredDiscoveryEvent eventData, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class ThrowingDiscoveryHandler : ILocalEventHandler<ThrowingDiscoveryEvent>
{
    public ThrowingDiscoveryHandler(DiscoveryActivationState state)
    {
        state.Constructions++;
        throw new InvalidOperationException("Handler construction failed.");
    }

    public Task HandleEventAsync(ThrowingDiscoveryEvent eventData, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class AsyncOnlyDiscoveryHandler : ILocalEventHandler<DiscoveryProbeEvent>, IAsyncDisposable
{
    private readonly DiscoveryActivationState _state;

    public AsyncOnlyDiscoveryHandler(DiscoveryActivationState state)
    {
        _state = state;
        _state.Constructions++;
    }

    public Task HandleEventAsync(DiscoveryProbeEvent eventData, CancellationToken cancellationToken)
    {
        _state.Deliveries++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _state.HandlerDisposals++;
        return ValueTask.CompletedTask;
    }
}

public sealed class AsyncOnlyDiscoveryDependency : IAsyncDisposable
{
    private readonly DiscoveryActivationState _state;

    public AsyncOnlyDiscoveryDependency(DiscoveryActivationState state)
    {
        _state = state;
        _state.DependencyConstructions++;
    }

    public ValueTask DisposeAsync()
    {
        _state.DependencyDisposals++;
        return ValueTask.CompletedTask;
    }
}

public sealed class AsyncDependentDiscoveryHandler : ILocalEventHandler<DiscoveryProbeEvent>
{
    private readonly DiscoveryActivationState _state;

    public AsyncDependentDiscoveryHandler(DiscoveryActivationState state, AsyncOnlyDiscoveryDependency dependency)
    {
        _state = state;
        _state.Constructions++;
    }

    public Task HandleEventAsync(DiscoveryProbeEvent eventData, CancellationToken cancellationToken)
    {
        _state.Deliveries++;
        return Task.CompletedTask;
    }
}

public sealed class AutoDiscoveredHandler :
    ILocalEventHandler<LifecycleEvent>,
    ILocalEventHandler<SecondaryLifecycleEvent>
{
    public Task HandleEventAsync(LifecycleEvent eventData, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task HandleEventAsync(SecondaryLifecycleEvent eventData, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class SubscriptionProviderProbe(IEventSubscriptionRegistry registry) : IHostedService
{
    public int SubscriptionCountAtStart { get; private set; }

    public int SubscriptionCountAtStop { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SubscriptionCountAtStart = registry.GetAll().Count();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        SubscriptionCountAtStop = registry.GetAll().Count();
        return Task.CompletedTask;
    }
}

public sealed class FailingStartService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => Task.FromException(new InvalidOperationException("Provider startup failed."));

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SubscriptionChangeRecorder : IObserver<EventSubscriptionChange>
{
    private readonly Lock _gate = new();
    private readonly List<EventSubscriptionChange> _changes = [];

    public IReadOnlyList<EventSubscriptionChange> Changes
    {
        get
        {
            lock (_gate)
            {
                return _changes.ToArray();
            }
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(EventSubscriptionChange value)
    {
        lock (_gate)
        {
            _changes.Add(value);
        }
    }
}

internal sealed class ControlledBatchRegistry(bool blockAfterFirstSubscription) : IEventSubscriptionRegistry
{
    private readonly EventSubscriptionRegistry _inner =
        new(NullLogger<EventSubscriptionRegistry>.Instance);
    private readonly TaskCompletionSource _batchEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _batchRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<EventSubscriptionId> _ownedCreationOrder = [];
    private readonly List<EventSubscriptionId> _rollbackOrder = [];
    private readonly List<EventSubscriptionId> _unsubscribeOrder = [];

    public Task BatchEntered => _batchEntered.Task;

    public IReadOnlyList<EventSubscriptionId> OwnedCreationOrder => _ownedCreationOrder;

    public IReadOnlyList<EventSubscriptionId> RollbackOrder => _rollbackOrder;

    public IReadOnlyList<EventSubscriptionId> UnsubscribeOrder => _unsubscribeOrder;

    public void ReleaseBatch() => _batchRelease.TrySetResult();

    public Task<IEventSubscription> SubscribeAsync(
        EventSubscriptionDescriptor descriptor,
        CancellationToken cancellationToken = default)
        => _inner.SubscribeAsync(descriptor, cancellationToken);

    public async Task<IReadOnlyList<IEventSubscription>> SubscribeBatchAsync(
        IEnumerable<EventSubscriptionDescriptor> descriptors,
        CancellationToken cancellationToken = default)
    {
        var created = new List<IEventSubscription>();
        try
        {
            foreach (var descriptor in descriptors)
            {
                var subscription = await _inner.SubscribeAsync(descriptor, cancellationToken);
                created.Add(subscription);
                _ownedCreationOrder.Add(subscription.Id);

                if (created.Count == 1)
                {
                    _batchEntered.TrySetResult();
                    if (blockAfterFirstSubscription)
                    {
                        await _batchRelease.Task.WaitAsync(cancellationToken);
                    }
                }
            }

            return created;
        }
        catch
        {
            foreach (var subscription in created.AsEnumerable().Reverse())
            {
                await _inner.UnsubscribeAsync(subscription.Id, CancellationToken.None);
                _rollbackOrder.Add(subscription.Id);
            }

            throw;
        }
    }

    public async Task UnsubscribeAsync(
        EventSubscriptionId subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await _inner.UnsubscribeAsync(subscriptionId, cancellationToken);
        _unsubscribeOrder.Add(subscriptionId);
    }

    public Task UnsubscribeBatchAsync(
        IEnumerable<EventSubscriptionId> subscriptionIds,
        CancellationToken cancellationToken = default)
        => UnsubscribeSequentiallyAsync(subscriptionIds, cancellationToken);

    public Task UnsubscribeWhereAsync(
        Func<IEventSubscription, bool> predicate,
        CancellationToken cancellationToken = default)
        => UnsubscribeSequentiallyAsync(
            _inner.GetAll().Where(predicate).Select(subscription => subscription.Id),
            cancellationToken);

    public IQueryable<IEventSubscription> GetAll() => _inner.GetAll();

    public IEventSubscription? GetById(EventSubscriptionId subscriptionId) => _inner.GetById(subscriptionId);

    public IReadOnlyList<IEventSubscription> GetByServiceKey(string? serviceKey) => _inner.GetByServiceKey(serviceKey);

    public IReadOnlyList<IEventSubscription> GetByTopicName(string topicName) => _inner.GetByTopicName(topicName);

    public IReadOnlyList<IEventSubscription> GetByEventType(Type eventType) => _inner.GetByEventType(eventType);

    public IReadOnlyList<IEventSubscription> GetByState(EventSubscriptionState state) => _inner.GetByState(state);

    public IReadOnlyList<IEventSubscription> GetByScope(EventSubscriptionScope scope) => _inner.GetByScope(scope);

    public Task ActivateAsync(
        EventSubscriptionId subscriptionId,
        CancellationToken cancellationToken = default)
        => _inner.ActivateAsync(subscriptionId, cancellationToken);

    public Task DeactivateAsync(
        EventSubscriptionId subscriptionId,
        CancellationToken cancellationToken = default)
        => _inner.DeactivateAsync(subscriptionId, cancellationToken);

    public Task ReactivateAsync(
        EventSubscriptionId subscriptionId,
        CancellationToken cancellationToken = default)
        => _inner.ReactivateAsync(subscriptionId, cancellationToken);

    public Task UnsubscribeByEventTypeAsync(
        Type eventType,
        CancellationToken cancellationToken = default)
        => UnsubscribeSequentiallyAsync(
            _inner.GetByEventType(eventType).Select(subscription => subscription.Id),
            cancellationToken);

    public Task UnsubscribeByHandlerTypeAsync(
        Type handlerType,
        CancellationToken cancellationToken = default)
        => UnsubscribeSequentiallyAsync(
            _inner.GetAll()
                .Where(subscription => subscription.HandlerType == handlerType)
                .Select(subscription => subscription.Id),
            cancellationToken);

    public Task UnsubscribeByServiceKeyAsync(
        string? serviceKey,
        CancellationToken cancellationToken = default)
        => UnsubscribeSequentiallyAsync(
            _inner.GetByServiceKey(serviceKey).Select(subscription => subscription.Id),
            cancellationToken);

    public IDisposable Subscribe(IObserver<EventSubscriptionChange> observer) => _inner.Subscribe(observer);

    private async Task UnsubscribeSequentiallyAsync(
        IEnumerable<EventSubscriptionId> subscriptionIds,
        CancellationToken cancellationToken)
    {
        foreach (var subscriptionId in subscriptionIds.ToArray())
        {
            await UnsubscribeAsync(subscriptionId, cancellationToken);
        }
    }
}
