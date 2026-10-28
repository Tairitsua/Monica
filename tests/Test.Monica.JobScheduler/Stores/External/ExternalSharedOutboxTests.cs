using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.EntityFrameworkCore.Extensions;
using Monica.EntityFrameworkCore.Models;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.Repository.Outbox.Models;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Models;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

public sealed partial class ExternalOutboxLeaseTests
{
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
