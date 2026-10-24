using System.Net.Http.Json;
using System.Text.Json;
using Domains.Ordering.Configurations;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.ApplicationModel.Abstractions;
using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Core.Results;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.Providers;
using Monica.Reference.Api.Configurations;
using Platform.Protocol.Application;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Contracts;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Events;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Models;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Requests;
using Roles.Ordering.HandlersEvent;
using Roles.Ordering.Reporting.BackgroundWorkers;
using Xunit;

namespace Test.Monica.Reference.Api.Composition;

public sealed class ReferenceApplicationTests
{
    [Fact]
    public async Task StartupDiagnostics_ShouldExposeActualRoleDocumentsInTheProductionHost()
    {
        using var factory = new ReferenceApiTestApplicationFactory();
        var token = TestContext.Current.CancellationToken;
        await using var app = await factory.CreateAsync(cancellationToken: token);
        using var client = ((TestServer)app.Services.GetRequiredService<IServer>()).CreateClient();
        using var ordering = JsonDocument.Parse(await client.GetStringAsync("/swagger/role.ordering/swagger.json", token));
        using var reporting = JsonDocument.Parse(await client.GetStringAsync("/swagger/role.ordering.reporting/swagger.json", token));
        var orderingPaths = ordering.RootElement.GetProperty("paths");
        var reportingPaths = reporting.RootElement.GetProperty("paths");
        Assert.True(orderingPaths.TryGetProperty("/api/v1/Ordering/commands/CreateOrder", out _));
        Assert.False(orderingPaths.TryGetProperty("/api/v1/Ordering/queries/GetOrderSummary", out _));
        Assert.True(reportingPaths.TryGetProperty("/api/v1/Ordering/queries/GetOrderSummary", out _));
        Assert.False(reportingPaths.TryGetProperty("/api/v1/Ordering/commands/CreateOrder", out _));
    }

    [Fact]
    public async Task ManualHost_ShouldKeepRealApiAndLocalEventBehaviorWithoutPublishingConfiguration()
    {
        using var factory = new ReferenceApiTestApplicationFactory();
        var token = TestContext.Current.CancellationToken;
        await using var app = await factory.CreateAsync(cancellationToken: token);
        var model = app.Services.GetRequiredService<IApplicationModel>();
        Assert.Equal("monica-ordering-reference", model.AppId);
        Assert.Equal("Monica Ordering Reference", app.Application.Application.ResolveAppName());
        Assert.True(model.IsRoleActive(ReferenceRoles.Ordering));
        Assert.True(model.IsRoleActive(ReferenceRoles.OrderingReporting));
        Assert.Equal(4, model.Operations.Count);
        Assert.False(app.Application.Application.AutonomousWork);

        await using var scope = app.CreateScope(token);
        var commands = scope.Resolve<IOrderingCommandApi>();
        var queries = scope.Resolve<IOrderingQueryApi>();
        var created = await commands.CreateOrder(new CommandCreateOrder("MANUAL-1003", "Manual customer", 125m), token);
        Assert.Equal(ResStatus.Ok, created.Status);
        Assert.NotNull(created.Data);
        var approved = await commands.ApproveOrder(new CommandApproveOrder(created.Data.Id), token);
        Assert.Equal(ResStatus.Ok, approved.Status);
        Assert.Equal(OrderStatus.Approved, approved.Data!.Status);
        var listed = await queries.GetOrders(new QueryGetOrders(), token);
        Assert.Equal(ResStatus.Ok, listed.Status);
        Assert.Contains(listed.Data!, order => order.Id == created.Data.Id && order.Status == OrderStatus.Approved);
        var summary = await queries.GetOrderSummary(new QueryGetOrderSummary(), token);
        Assert.Equal(ResStatus.Ok, summary.Status);
        Assert.Equal(3, summary.Data!.TotalOrders);
        Assert.Equal(2, summary.Data.DraftOrders);
        Assert.Contains(factory.Logs.Entries, entry => entry.Category == typeof(LocalEventHandlerOrderApproved).FullName
            && entry.Message.Contains("MANUAL-1003", StringComparison.Ordinal));
        Assert.All(app.Services.GetRequiredService<IEventSubscriptionRegistry>().GetByEventType(typeof(EventOrderApproved)),
            subscription => Assert.Equal(EventSubscriptionState.Active, subscription.State));

        using var client = ((TestServer)app.Services.GetRequiredService<IServer>()).CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/Ordering/queries/GetOrders", new QueryGetOrders(), token);
        response.EnsureSuccessStatusCode();
        Assert.Contains("MANUAL-1003", await response.Content.ReadAsStringAsync(token));
        Assert.False(Directory.Exists(factory.StoreDirectory));
    }

    [Fact]
    public async Task ManualHost_ShouldExecuteProductionBacklogJobOnlyAfterNativeRunNow()
    {
        using var factory = new ReferenceApiTestApplicationFactory();
        var token = TestContext.Current.CancellationToken;
        await using var app = await factory.CreateAsync(cancellationToken: token);
        var store = Assert.IsType<InMemoryJobSchedulerStore>(app.Services.GetRequiredService<IJobSchedulerStore>());
        var scope = app.Application.Application.DeploymentScope!;
        Assert.Equal("monica-reference-ordering", scope);
        Assert.Equal(0, (await store.QueryExecutionsAsync(new JobExecutionQuery { SchedulerScopeKey = scope }, token)).TotalCount);
        Assert.Contains(app.Application.ActivationResources, resource => resource.ImplementationType == typeof(WorkerOrderBacklogReport)
            && resource.Mode == ActivationMode.Manual);

        await using var request = app.CreateScope(token);
        var facade = request.Resolve<JobSchedulerFacade>();
        var admitted = await facade.RunRecurringNowAsync(new JobRecurringRunNowRequest
        {
            OwnerKey = ReferenceRoles.OrderingReporting,
            JobKey = typeof(WorkerOrderBacklogReport).FullName!,
            InstanceId = "reference-explicit-report"
        }, token);
        Assert.Equal(ResStatus.Ok, admitted.Status);
        Assert.NotNull(admitted.Data);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        JobExecutionInstance? execution;
        do
        {
            execution = await store.GetExecutionAsync(scope, admitted.Data.InstanceId, timeout.Token);
            if (execution?.IsTerminal != true) await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        } while (execution?.IsTerminal != true);

        Assert.NotNull(execution);
        Assert.Equal(JobExecutionState.Succeeded, execution.State);
        Assert.Equal(JobExecutionOrigin.RecurringRunNow, execution.Origin);
        Assert.Contains(execution.History, entry => entry.Kind == JobExecutionHistoryKind.ExecutionLog
            && entry.Message.Contains("2 draft order(s) out of 2", StringComparison.Ordinal));
        Assert.Equal(1, (await store.QueryExecutionsAsync(new JobExecutionQuery { SchedulerScopeKey = scope }, token)).TotalCount);
        Assert.False(Directory.Exists(factory.StoreDirectory));
    }

    [Fact]
    public async Task RenamedOptions_ShouldRetainManagedIdentityAndKeepStartupReadOnly()
    {
        using var factory = new ReferenceApiTestApplicationFactory();
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<ConfigurationFacade>();
        var catalog = await facade.GetDefinitionCatalogAsync();
        Assert.Equal(ResStatus.Ok, catalog.Status);
        var managed = Assert.Single(catalog.Data!.Definitions, definition => definition.DefinitionKey == "reference.ordering");
        Assert.Equal("Ordering", managed.SectionPath);
        Assert.Equal(ConfigurationKind.Managed, managed.Kind);
        Assert.Equal(100, scope.Resolve<IOptions<OptionsOrdering>>().Value.MaximumOrdersReturned);
        var state = await facade.GetDefinitionStateAsync(managed.DefinitionKey);
        Assert.Equal(ResStatus.Ok, state.Status);
        Assert.True(state.Data!.IsUnpersisted);
        Assert.Null(state.Data.EffectiveValueVersion);
        var startup = Assert.Single(catalog.Data.Definitions, definition => definition.SectionPath == "ReferenceUI");
        Assert.Equal(ConfigurationKind.Startup, startup.Kind);
        Assert.False(startup.CanMutate);
        Assert.True(scope.Resolve<IOptions<OptionsReferenceUI>>().Value.EnableDiagnostics);
        Assert.False(Directory.Exists(factory.StoreDirectory));
    }
}
