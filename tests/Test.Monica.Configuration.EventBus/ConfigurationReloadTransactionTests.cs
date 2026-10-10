using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.EventBus.Modules;
using Monica.Configuration.EventBus.Services;
using Monica.Configuration.Models;
using Monica.Core.Execution;
using Monica.Core.Modularity.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.EventBus;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.EventBus.Services;
using Monica.EventBus.Services.Support;
using Monica.Modules;
using Monica.Repository.Persistence.Models;
using Monica.Repository.Persistence.Services;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Configuration.EventBus;

public sealed class ConfigurationReloadTransactionTests
{
    private const string TOPIC = "test.configuration.reload";
    private const string SERVICE_KEY = "configuration-reload";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotifyAsync_WhenMultipleWriteContextsExist_ShouldForwardWithoutTransactionOrDuplicateDiscovery(bool keyed)
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("monica-reload-transaction-");
        try
        {
            await using var application = await new ReloadApplicationFactory(directory.FullName, keyed)
                .CreateAsync(cancellationToken: token);
            var registrations = application.Services.GetServices<RepositoryDbContextRegistration>()
                .Where(registration => registration.ProviderType == DbContextProviderType.UnitOfWork).ToArray();
            Assert.Equal(2, registrations.Length);
            var subscriptions = application.Services.GetRequiredService<IEventSubscriptionRegistry>();
            var subscription = Assert.Single(subscriptions.GetByEventType(typeof(ConfigurationReloadSignal)));
            Assert.Equal(TOPIC, subscription.TopicName);
            Assert.Equal(keyed ? SERVICE_KEY : null, subscription.ServiceKey);
            Assert.Equal(EventSubscriptionScope.Distributed, subscription.Scope);
            Assert.Equal(EventSubscriptionState.Active, subscription.State);
            Assert.False(subscription.IsAutoDiscovered);
            Assert.NotNull(subscription.HandlerType);
            Assert.False(application.Services.GetRequiredService<IServiceProviderIsService>()
                .IsService(subscription.HandlerType!));
            var recorder = application.Services.GetRequiredService<ReloadRecorder>();
            var provider = application.Services.GetRequiredService<LoopbackConfigurationBus>();
            if (!keyed)
                Assert.Same(provider, application.Services.GetRequiredService<IEventTransport>());
            var notifier = Assert.Single(application.Services.GetServices<IConfigurationChangeNotifier>()
                .OfType<ConfigurationEventBusChangeNotifier>());
            var first = Signal("first");
            var second = Signal("second");

            await notifier.NotifyAsync(first, token);
            await notifier.NotifyAsync(second, token);

            Assert.Equal(new object[] { first, second }, provider.PublishedEvents);
            Assert.Equal(new[] { first, second }, recorder.Signals);
            Assert.All(recorder.Tokens, cancellationToken => Assert.Equal(token, cancellationToken));
            Assert.Equal(2, recorder.DeliveryScopes.Count);
            Assert.Equal(2, recorder.DeliveryScopes.Distinct().Count());
            Assert.Equal(recorder.DeliveryScopes, recorder.DisposedScopes);
            Assert.All(recorder.ActiveTransactions, active => Assert.False(active));

            // An ordinary delegate still selects the automatic boundary and fails before invocation on this host.
            // The bridge fix must not disable UnitOfWork or change application-handler defaults.
            await using (var publishingScope = application.Services.CreateAsyncScope())
            {
                var eventBus = keyed
                    ? publishingScope.ServiceProvider.GetRequiredKeyedService<IDistributedEventBus>(SERVICE_KEY)
                    : publishingScope.ServiceProvider.GetRequiredService<IDistributedEventBus>();
                Assert.IsType<ScopedDistributedEventBusGateway>(eventBus);
                var controlInvoked = false;
                await using var control = await eventBus.SubscribeAsync<ConfigurationReloadSignal>((_, _) =>
                {
                    controlInvoked = true;
                    return Task.CompletedTask;
                }, "test.automatic.control");
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    eventBus.PublishAsync(Signal("control"), "test.automatic.control", token));
                Assert.Contains("Select the operation's DbContext explicitly", error.Message);
                Assert.False(controlInvoked);
            }

            var subscriber = Assert.Single(application.Services.GetServices<IHostedService>()
                .OfType<ConfigurationEventBusSubscriptionHostedService>());
            await subscriber.StopAsync(token);
            Assert.Equal(EventSubscriptionState.Disposed, subscription.State);
            await notifier.NotifyAsync(Signal("after-stop"), token);
            Assert.Equal(2, recorder.Signals.Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ConfigurationReloadSignal Signal(string id) => new()
    {
        SignalId = id,
        OriginInstanceId = "remote",
        StoreKey = "file:test",
        Kind = ConfigurationReloadSignalKind.ReloadAll,
        ChangedTime = DateTimeOffset.UnixEpoch
    };

    private sealed class ReloadApplicationFactory(string directory, bool keyed)
        : MonicaTestApplicationFactory<ConfigurationReloadTransactionTests>
    {
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies =>
            [typeof(ConfigurationEventBusSubscriptionHostedService).Assembly];

        protected override void ConfigureMonica(IMonicaBuilder monica)
        {
            var plan = MonicaConfigurationInputPlan.Create(inputs =>
                inputs.UseFileConfigurationStore(options => options.RootDirectory = directory));
            monica.AddConfiguration(plan).UseEventBus(options =>
            {
                options.TopicName = TOPIC;
                options.DistributedEventBusServiceKey = keyed ? SERVICE_KEY : null;
            });
            monica.AddEventBus().UseDistributedEventBus<LoopbackConfigurationBus>().ConfigureServices(context =>
            {
                if (keyed)
                {
                    context.Services.AddKeyedScoped<IDistributedEventBus>(SERVICE_KEY, (services, _) =>
                        new ScopedDistributedEventBusGateway(services.GetRequiredService<LoopbackConfigurationBus>(),
                            services.GetRequiredService<IEventMessageFactory>(), services, SERVICE_KEY));
                }
            });
            monica.AddRepository()
                .AddRepositoryDbContext<FirstWriteContext>((_, options) => options.UseSqlite("Data Source=:memory:"))
                .AddRepositoryDbContext<SecondWriteContext>((_, options) => options.UseSqlite("Data Source=:memory:"));
            monica.AddExecutionPipeline().AddBehavior<DeliveryProbeBehavior>(
                descriptorFilter: descriptor => descriptor.Point == EventBusExecutionPoints.DistributedHandler,
                lifetime: ServiceLifetime.Scoped);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            if (!keyed)
            {
                // Restore the distributed provider after the standard seams install their recording bus.
                services.Replace(ServiceDescriptor.Singleton<IEventTransport>(provider =>
                    provider.GetRequiredService<LoopbackConfigurationBus>()));
                services.Replace(ServiceDescriptor.Scoped<IDistributedEventBus>(provider =>
                    new ScopedDistributedEventBusGateway(provider.GetRequiredService<LoopbackConfigurationBus>(),
                        provider.GetRequiredService<IEventMessageFactory>(), provider)));
            }
            services.AddSingleton<ReloadRecorder>();
            services.AddScoped<AsyncDeliveryScopeProbe>();
            services.Replace(ServiceDescriptor.Singleton<IConfigurationReloadSignalReceiver, RecordingReloadReceiver>());
        }
    }

    public sealed class FirstWriteContext(DbContextOptions<FirstWriteContext> options, ICachedServiceProvider services)
        : RepositoryDbContext<FirstWriteContext>(options, services);

    public sealed class SecondWriteContext(DbContextOptions<SecondWriteContext> options, ICachedServiceProvider services)
        : RepositoryDbContext<SecondWriteContext>(options, services);

    public sealed class LoopbackConfigurationBus(
        IServiceScopeFactory scopeFactory,
        IEventHandlerInvoker invoker,
        IEventSubscriptionRegistry subscriptions,
        ILoggerFactory loggerFactory,
        IOptions<ModuleConfigurationEventBusOption> options)
        : DistributedEventBusBase(scopeFactory, invoker, subscriptions, loggerFactory,
            options.Value.DistributedEventBusServiceKey), IEventTransport
    {
        public List<object> PublishedEvents { get; } = [];

        public override Task PublishAsync(Type eventType, object eventData, string? topicName = null,
            CancellationToken cancellationToken = default)
        {
            PublishedEvents.Add(eventData);
            return TriggerHandlersAsync(eventType, eventData, ResolveTopicName(eventType, topicName), cancellationToken);
        }

        public override async Task BulkPublishAsync(Type eventType, IEnumerable<object> eventDataList,
            string? topicName = null, CancellationToken cancellationToken = default)
        {
            foreach (var item in eventDataList)
                await PublishAsync(eventType, item, topicName, cancellationToken);
        }

        public Task SendAsync(EventMessage message, CancellationToken cancellationToken)
            => throw new NotSupportedException("This scenario sends only non-durable reload signals.");
    }

    public sealed class ReloadRecorder
    {
        public int NextScope;
        public List<ConfigurationReloadSignal> Signals { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public List<int> DeliveryScopes { get; } = [];
        public List<int> DisposedScopes { get; } = [];
        public List<bool> ActiveTransactions { get; } = [];
    }

    public sealed class RecordingReloadReceiver(ReloadRecorder recorder) : IConfigurationReloadSignalReceiver
    {
        public Task ReceiveAsync(ConfigurationReloadSignal signal, CancellationToken cancellationToken)
        {
            recorder.Signals.Add(signal);
            recorder.Tokens.Add(cancellationToken);
            return Task.CompletedTask;
        }
    }

    public sealed class AsyncDeliveryScopeProbe(ReloadRecorder recorder) : IAsyncDisposable
    {
        public int Id { get; } = Interlocked.Increment(ref recorder.NextScope);

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            recorder.DisposedScopes.Add(Id);
        }
    }

    public sealed class DeliveryProbeBehavior(ReloadRecorder recorder, IUnitOfWorkManager manager, AsyncDeliveryScopeProbe probe)
        : IExecutionBehavior<ConfigurationReloadSignal, ExecutionUnit>
    {
        public Task<ExecutionUnit> ExecuteAsync(ExecutionContext<ConfigurationReloadSignal> context,
            ExecutionDelegate<ExecutionUnit> next)
        {
            recorder.ActiveTransactions.Add(manager.Current is not null);
            recorder.DeliveryScopes.Add(probe.Id);
            return next();
        }
    }
}
