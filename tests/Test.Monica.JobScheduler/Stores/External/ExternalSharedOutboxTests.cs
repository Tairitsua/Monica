using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Monica.EntityFrameworkCore.Extensions;
using Monica.EntityFrameworkCore.Models;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.Repository.Outbox.Models;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Models;
using Monica.Locker.Leases;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

public sealed partial class ExternalOutboxLeaseTests
{
    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task StoreMetadataAndRetry_WhenScalarQueryRootsAreUnsupported_ShouldUseTheOwnedConnection(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateWithLayoutAsync(provider,
            InfrastructureTableLayout.PreferSchema("infra"), token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            // Sharded application hosts can select an external maintenance authority. The native Outbox itself
            // must resolve its physical table and retry clock without requiring unmapped scalar query roots.
            using var host = CreateHost(fixture.Database, TimeProvider.System, new OutboxCommands(), false,
                transport: new FailingDeliveryTransport(), configureOutbox: options => options.SharedStoreKey = "shared-messages",
                configureDbContext: options => options.UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra"))
                    .AddInterceptors(new StoreScalarQueryGuard()), maintenanceStore: new InMemoryLeaseStore(TimeProvider.System));
            await CreateOutboxSchemaAsync(host, token);
            Assert.Equal(0, await Dispatcher(host).CleanupAsync(token));
            await using (var operation = host.Services.CreateAsyncScope())
            {
                var services = operation.ServiceProvider;
                await services.GetRequiredService<IUnitOfWorkManager>().RunAsync(
                    () => services.GetRequiredService<IDistributedEventBus>()
                        .PublishAsync(new SharedDeliveryNotice(1), cancellationToken: token),
                    new UnitOfWorkScopeOptions([typeof(ExternalOutboxContext)]), token);
            }
            Assert.Equal(0, await Dispatcher(host).DrainAsync(1, token));
            var row = Assert.Single(await ReadDeliveryAsync(host, token));
            Assert.Null(row.DeliveredAtUtc);
            Assert.Null(row.LeaseId);
            Assert.NotNull(row.NextAttemptAtUtc);
            Assert.Contains("delivery-test failure", row.LastError);
        });
    }

    private sealed class StoreScalarQueryGuard : ExpressionVisitor, IQueryExpressionInterceptor
    {
        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData) =>
            Visit(queryExpression);

        protected override Expression VisitExtension(Expression node)
        {
            if (node is QueryRootExpression root and not EntityQueryRootExpression
                && (root.ElementType == typeof(string) || root.ElementType == typeof(DateTime)))
                throw new InvalidOperationException("This query compiler cannot route unmapped store metadata or clock roots.");
            return base.VisitExtension(node);
        }
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task SharedDelivery_WhenTargetsDiffer_ShouldClaimOnlyQualifiedRowsInTheInfrastructureSchema(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var transport = new DeliveryTransport();
            using var host = CreateHost(fixture.Database, TimeProvider.System, new OutboxCommands(), false,
                transport: transport, configureOutbox: options => options.SharedStoreKey = "shared-messages",
                configureDbContext: options => options.UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra")));
            await CreateOutboxSchemaAsync(host, token);
            await using (var operation = host.Services.CreateAsyncScope())
            {
                var services = operation.ServiceProvider;
                await services.GetRequiredService<IUnitOfWorkManager>().RunAsync(async () =>
                {
                    for (var index = 0; index < 3; index++)
                        await services.GetRequiredService<IDistributedEventBus>()
                            .PublishAsync(new SharedDeliveryNotice(index), cancellationToken: token);
                }, new UnitOfWorkScopeOptions([typeof(ExternalOutboxContext)]), token);
            }
            await using (var seed = await Factory(host).CreateDbContextAsync(token))
            {
                Assert.Equal("infra", seed.Model.FindEntityType(typeof(OutboxMessage))!.GetSchema());
                seed.AddRange(new OutboxMessage { MessageId = "legacy", Source = "tests", EventName = "test",
                    TopicName = "test", Scope = EventSubscriptionScope.Distributed, CreatedAtUtc = DateTime.UtcNow },
                    new OutboxMessage { MessageId = "other-transport", Source = "tests", EventName = "test",
                    TopicName = "test", ServiceKey = "absent", DeliveryTarget = "D:" + new string('0', 64),
                    Scope = EventSubscriptionScope.Distributed, CreatedAtUtc = DateTime.UtcNow });
                await seed.SaveChangesAsync(token);
            }
            Assert.Equal(3, await Dispatcher(host).DrainAsync(10, token));
            Assert.Equal(0, await Dispatcher(host).DrainAsync(10, token));
            Assert.Equal(3, transport.MessageIds.Count);
            var rows = await ReadDeliveryAsync(host, token);
            Assert.Equal(3, rows.Count(row => row.DeliveredAtUtc is not null));
            Assert.All(rows.Where(row => row.DeliveredAtUtc is null), row =>
            {
                Assert.Equal(0, row.Attempts);
                Assert.Null(row.LeaseId);
                Assert.Null(row.LastError);
            });
        });
    }

    [Outbox, EventName("tests.external-shared-delivery.v1")]
    public sealed record SharedDeliveryNotice(int Index);
}
