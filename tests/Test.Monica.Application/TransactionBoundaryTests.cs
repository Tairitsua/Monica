using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.Application.Abstractions;
using Monica.Authority.Identity.Abstractions;
using Monica.Authority.Identity.Services;
using Monica.Core.Execution;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.DependencyInjection.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.Invocation.Models;
using Monica.Modules;
using Monica.Repository.Inbox.Annotations;
using Monica.Repository.Inbox.Models;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Persistence.Services;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Models;
using Monica.Testing.Hosting;
using Test.Monica.Application.PublishedLanguages.DomainTesting.Contracts;
using Test.Monica.Application.PublishedLanguages.DomainTesting.Requests;
using Xunit;

namespace Test.Monica.Application;

public sealed class TransactionBoundaryTests
{
    [Theory]
    [InlineData(false, false, 2)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    public async Task Shared_WhenNestedContextsUseOneConnection_ShouldCommitOrRollbackTogether(bool failParent, bool failChild, int rows)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        var result = await caller.Resolve<ITestingCommandApi>().Coordinate(new(FailParent: failParent, FailChild: failChild), token);
        Assert.Equal(rows == 0 ? ResStatus.InternalError : ResStatus.Ok, result.Status);
        if (rows > 0)
        {
            Assert.Equal(result.Data!.ScopeId, result.Data.ChildScopeId);
            Assert.Equal(result.Data.ScopeId, result.Data.GrandchildScopeId);
        }
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) => Assert.Equal(rows, await db.Entries.CountAsync(ct)), token);
    }

    [Fact]
    public async Task Shared_WhenNestedConnectionIsIndependent_ShouldRejectBeforeChildAndRollbackParent()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory(sharedConnection: false).CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        var result = await caller.Resolve<ITestingCommandApi>().Coordinate(new(), token);
        Assert.Equal(ResStatus.InternalError, result.Status);
        Assert.Equal(0, app.Services.GetRequiredService<TransactionLedger>().ChildCalls);
        await AssertRowsAsync(app, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_WhenParentFails_ShouldPreserveChildCommitAndRestoreParent(bool callOriginalAfterView)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory(sharedConnection: false).CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        using var identity = app.Services.GetRequiredService<CurrentPrincipalContext>().Use(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "original-caller")], "test")));
        var result = await caller.Resolve<ITestingCommandApi>()
            .Coordinate(new(Independent: true, FailParent: true, CallOriginalAfterView: callOriginalAfterView), token);
        Assert.Equal(ResStatus.InternalError, result.Status);
        var ledger = app.Services.GetRequiredService<TransactionLedger>();
        Assert.Equal(1, ledger.ChildCalls);
        Assert.NotEqual(ledger.ParentScope, ledger.ChildScope);
        Assert.Equal(ledger.ChildScope, ledger.GrandchildScope);
        Assert.Equal("original-caller", ledger.ChildUser);
        Assert.True(ledger.ParentRestored);
        Assert.Equal(callOriginalAfterView, ledger.OriginalRejected);
        Assert.Null(app.Services.GetRequiredService<ExecutionScopeContext>().Services);
        await AssertRowsAsync(app, 0, 1);
    }

    [Fact]
    public async Task Independent_WhenChildFailsAndIsHandled_ShouldAllowParentCommit()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory(sharedConnection: false).CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        var result = await caller.Resolve<ITestingCommandApi>().Coordinate(new(Independent: true, FailChild: true), token);
        Assert.Equal(ResStatus.Ok, result.Status);
        Assert.True(app.Services.GetRequiredService<TransactionLedger>().ParentRestored);
        await AssertRowsAsync(app, 1, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receive_WhenHandlerCallsGeneratedApi_ShouldKeepInboxBusinessAndOutboxAtomic(bool failFirst)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        await caller.Resolve<IDistributedEventBus>().SubscribeAsync<ForeignTransactionNotice, TransactionNoticeHandler>();
        var message = caller.Resolve<IEventMessageFactory>().Prepare(typeof(ForeignTransactionNotice), new ForeignTransactionNotice(),
            EventSubscriptionScope.Distributed);
        var ledger = app.Services.GetRequiredService<TransactionLedger>();
        ledger.FailEvent = failFirst;
        if (failFirst)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => caller.Resolve<IEventReceiveDispatcher>().DispatchAsync(message, token));
            await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) =>
            {
                Assert.Empty(await db.Entries.ToListAsync(ct));
                Assert.Empty(await db.Set<InboxReceipt>().ToListAsync(ct));
                Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(ct));
            }, token);
        }
        ledger.FailEvent = false;
        await caller.Resolve<IEventReceiveDispatcher>().DispatchAsync(message, token);
        await caller.Resolve<IEventReceiveDispatcher>().DispatchAsync(message, token);
        Assert.Equal(failFirst ? 2 : 1, ledger.ChildCalls);
        Assert.Equal(ledger.EventScope, ledger.ChildScope);
        Assert.Equal(ledger.ChildScope, ledger.GrandchildScope);
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) =>
        {
            Assert.Single(await db.Entries.ToListAsync(ct));
            Assert.Single(await db.Set<InboxReceipt>().ToListAsync(ct));
            Assert.Single(await db.Set<OutboxMessage>().ToListAsync(ct));
        }, token);
    }

    private static async Task AssertRowsAsync(MonicaTestApplication app, int primary, int secondary)
    {
        var token = TestContext.Current.CancellationToken;
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) => Assert.Equal(primary, await db.Entries.CountAsync(ct)), token);
        await app.VerifyAsync<TransactionSecondaryContext>(async (db, ct) => Assert.Equal(secondary, await db.Entries.CountAsync(ct)), token);
    }

    [Fact]
    public async Task Independent_WhenChildCancels_ShouldRestoreParentScopeAndLeaveItsTransactionUsable()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = await new TransactionFactory().CreateAsync(cancellationToken: token);
        await using var caller = app.CreateScope(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        app.Services.GetRequiredService<TransactionLedger>().ChildCancellation = cancellation;
        var queries = caller.Resolve<ITestingQueryApi>();
        await caller.Resolve<IUnitOfWorkManager>().RunAsync(async () =>
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queries.WithTransaction(ApiTransaction.Independent)
                .TransactionScope(new(Cancel: true), cancellation.Token));
            var restored = await queries.TransactionScope(new(), token);
            Assert.Equal(caller.Resolve<TransactionScope>().Id, restored.Data);
            caller.Resolve<TransactionPrimaryContext>().Entries.Add(new OperationEntry { Name = "parent survived" });
        }, new UnitOfWorkScopeOptions([typeof(TransactionPrimaryContext)]), token);
        Assert.Null(app.Services.GetRequiredService<ExecutionScopeContext>().Services);
        await app.VerifyAsync<TransactionPrimaryContext>(async (db, ct) => Assert.Single(await db.Entries.ToListAsync(ct)), token);
    }
}

internal sealed class TransactionFactory(bool sharedConnection = true) : MonicaTestApplicationFactory<CommandHandlerCoordinate>
{
    protected override void ConfigureMonica(IMonicaBuilder builder)
    {
        builder.AddApplication();
        builder.AddApiClient().ConfigDomainInfoProvider(new TestingRpcDomains()).UseLocalTransport();
        builder.AddEventBus().UseNoOpDistributedEventBus();
        builder.AddRepository()
            .AddRepositoryDbContext<TransactionPrimaryContext>((_, options) => options.UseSqlite("Data Source=unused"))
            .AddRepositoryDbContext<TransactionSecondaryContext>((services, options) =>
            {
                if (sharedConnection) options.UseSqlite(services.GetRequiredService<TransactionPrimaryContext>().Database.GetDbConnection());
                else options.UseSqlite("Data Source=unused");
            })
            .AddInbox<TransactionPrimaryContext>()
            .AddOutbox<TransactionPrimaryContext>(options => options.EnableWorker = false);
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.UseTestDatabase<TransactionPrimaryContext>();
        if (!sharedConnection) services.UseTestDatabase<TransactionSecondaryContext>();
        services.AddSingleton<TransactionLedger>();
        services.AddScoped<TransactionScope>();
        services.AddScoped<TransactionNoticeHandler>();
    }
}

public sealed class TransactionPrimaryContext(DbContextOptions<TransactionPrimaryContext> options, ICachedServiceProvider services)
    : RepositoryDbContext<TransactionPrimaryContext>(options, services)
{
    public DbSet<OperationEntry> Entries => Set<OperationEntry>();
}

public sealed class TransactionSecondaryContext(DbContextOptions<TransactionSecondaryContext> options, ICachedServiceProvider services)
    : RepositoryDbContext<TransactionSecondaryContext>(options, services)
{
    public DbSet<OperationEntry> Entries => Set<OperationEntry>();
}

public sealed class TransactionScope
{
    public Guid Id { get; } = Guid.NewGuid();
}

public sealed class TransactionLedger
{
    public int ChildCalls { get; set; }
    public Guid ParentScope { get; set; }
    public Guid ChildScope { get; set; }
    public Guid GrandchildScope { get; set; }
    public Guid EventScope { get; set; }
    public string? ChildUser { get; set; }
    public bool ParentRestored { get; set; }
    public bool OriginalRejected { get; set; }
    public bool FailEvent { get; set; }
    public CancellationTokenSource? ChildCancellation { get; set; }
}

public sealed class CommandHandlerCoordinate(TransactionPrimaryContext db, TransactionScope scope, TransactionLedger ledger,
    ITestingCommandApi commands, ITestingQueryApi queries) : ApplicationService<CommandCoordinate, TransactionReceipt>
{
    [ExecutionTransaction(ExecutionTransactionMode.Automatic, DbContextTypes = [typeof(TransactionPrimaryContext)])]
    public override async Task<Res<TransactionReceipt>> Handle(CommandCoordinate request, CancellationToken cancellationToken)
    {
        ledger.ParentScope = scope.Id;
        db.Entries.Add(new OperationEntry { Name = "parent" });
        var view = commands.WithTransaction(request.Independent ? ApiTransaction.Independent : ApiTransaction.Shared);
        var child = await view.TransactionChild(new(request.FailChild), cancellationToken);
        ledger.ParentRestored = (await queries.TransactionScope(new(), cancellationToken)).Data == scope.Id;
        if (request.CallOriginalAfterView)
        {
            try { await commands.TransactionChild(new(), cancellationToken); }
            catch (NotSupportedException) { ledger.OriginalRejected = true; }
        }
        if (request.FailParent) throw new InvalidOperationException("Parent failed after its child.");
        // Deliberately handle a child's failed result to prove rollback-only versus independent behavior.
        return Res.Ok(new TransactionReceipt(scope.Id, child.Data?.ScopeId ?? Guid.Empty,
            child.Data?.GrandchildScopeId ?? Guid.Empty, child.Data?.User));
    }
}

public sealed class CommandHandlerTransactionChild(TransactionSecondaryContext db, TransactionScope scope,
    TransactionLedger ledger, ITestingQueryApi queries, ICurrentPrincipalAccessor principal)
    : ApplicationService<CommandTransactionChild, TransactionReceipt>
{
    [ExecutionTransaction(ExecutionTransactionMode.Automatic, DbContextTypes = [typeof(TransactionSecondaryContext)])]
    public override async Task<Res<TransactionReceipt>> Handle(CommandTransactionChild request, CancellationToken cancellationToken)
    {
        ledger.ChildCalls++;
        ledger.ChildScope = scope.Id;
        ledger.ChildUser = principal.Principal.Identity?.Name;
        ledger.GrandchildScope = (await queries.TransactionScope(new(), cancellationToken)).Data;
        db.Entries.Add(new OperationEntry { Name = "child" });
        await db.SaveChangesAsync(cancellationToken);
        if (request.Fail) return Res.Fail("Child failed after flushing.");
        return Res.Ok(new TransactionReceipt(scope.Id, Guid.Empty, ledger.GrandchildScope, ledger.ChildUser));
    }
}

public sealed class QueryHandlerTransactionScope(TransactionScope scope, TransactionLedger ledger) : ApplicationService<QueryTransactionScope, Guid>
{
    public override async Task<Res<Guid>> Handle(QueryTransactionScope request, CancellationToken cancellationToken)
    {
        if (request.Cancel)
        {
            await ledger.ChildCancellation!.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return Res.Ok(scope.Id);
    }
}

[EventName("foreign.transaction.requested")]
public sealed record ForeignTransactionNotice;

[Outbox, EventName("transaction.completed")]
public sealed record TransactionCompleted;

[Inbox("transaction.receiver")]
public sealed class TransactionNoticeHandler(ITestingCommandApi commands, IDistributedEventBus bus, TransactionScope scope, TransactionLedger ledger)
    : IDistributedEventHandler<ForeignTransactionNotice>
{
    [ExecutionTransaction(ExecutionTransactionMode.Automatic, DbContextTypes = [typeof(TransactionPrimaryContext)])]
    public async Task HandleEventAsync(ForeignTransactionNotice eventData, CancellationToken cancellationToken)
    {
        ledger.EventScope = scope.Id;
        var child = await commands.TransactionChild(new(), cancellationToken);
        if (child.Status != ResStatus.Ok) throw new InvalidOperationException("Child failed.");
        await bus.PublishAsync(new TransactionCompleted(), cancellationToken: cancellationToken);
        if (ledger.FailEvent) throw new InvalidOperationException("Receive failed after staging effects.");
    }
}
