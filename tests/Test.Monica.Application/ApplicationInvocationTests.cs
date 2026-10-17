using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Application.Abstractions;
using Test.Monica.Application.PublishedLanguages.DomainTesting.Contracts;
using Monica.Rpc.Models;
using Monica.Authority.Identity.Abstractions;
using Monica.Authority.Identity.Services;
using Monica.Core.Execution;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.DependencyInjection.Abstractions;
using Monica.Modules;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Hosting;
using Test.Monica.Application.PublishedLanguages.DomainTesting.Requests;
using Xunit;

namespace Test.Monica.Application;

public sealed class ApplicationInvocationTests
{
    [Fact]
    public async Task Send_WhenCircuitWritesTwice_ShouldUseFreshScopesAndRetainNestedScope()
    {
        await using var app = await new InvocationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var circuit = app.CreateScope(TestContext.Current.CancellationToken);
        circuit.Resolve<CircuitAuthentication>().User = Principal("first");
        var client = circuit.Resolve<ITestingCommandApi>();
        var first = await client.Store(new CommandStore("one"), TestContext.Current.CancellationToken);
        circuit.Resolve<CircuitAuthentication>().User = Principal("second");
        var second = await client.Store(new CommandStore("two"), TestContext.Current.CancellationToken);

        Assert.Equal(ResStatus.Ok, first.Status);
        Assert.Equal(ResStatus.Ok, second.Status);
        Assert.NotEqual(first.Data!.ScopeId, second.Data!.ScopeId);
        Assert.Equal(first.Data.ScopeId, first.Data.NestedScopeId);
        Assert.Equal(second.Data.ScopeId, second.Data.NestedScopeId);
        Assert.Equal("first", first.Data.User);
        Assert.Equal("second", second.Data.User);
        Assert.True(app.Services.GetRequiredService<ScopeLedger>().Disposed[first.Data.ScopeId]);
        Assert.True(app.Services.GetRequiredService<ScopeLedger>().Disposed[second.Data.ScopeId]);
        await app.VerifyAsync<OperationDbContext>(async (db, ct) => Assert.Equal(2, await db.Entries.CountAsync(ct)), TestContext.Current.CancellationToken);
        Assert.Null(app.Services.GetRequiredService<CurrentPrincipalContext>().Principal);
    }

    [Fact]
    public async Task Send_WhenConcurrent_ShouldIsolateScopesAndCallerContexts()
    {
        await using var app = await new InvocationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var firstCircuit = app.CreateScope(TestContext.Current.CancellationToken);
        await using var secondCircuit = app.CreateScope(TestContext.Current.CancellationToken);
        firstCircuit.Resolve<CircuitAuthentication>().User = Principal("alice");
        secondCircuit.Resolve<CircuitAuthentication>().User = Principal("bob");
        var calls = await Task.WhenAll(
            firstCircuit.Resolve<ITestingQueryApi>().Identity(new QueryIdentity(true), TestContext.Current.CancellationToken),
            secondCircuit.Resolve<ITestingQueryApi>().Identity(new QueryIdentity(true), TestContext.Current.CancellationToken));
        Assert.All(calls, result => Assert.Equal(ResStatus.Ok, result.Status));
        Assert.NotEqual(calls[0].Data!.ScopeId, calls[1].Data!.ScopeId);
        Assert.Equal("alice", calls[0].Data!.User);
        Assert.Equal("bob", calls[1].Data!.User);
    }

    [Fact]
    public async Task Send_WhenAnonymousOrDenied_ShouldNotUseAmbientSystemIdentity()
    {
        await using var app = await new InvocationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var ambient = app.Services.GetRequiredService<CurrentPrincipalContext>().Use(Principal("system"));
        await using var circuit = app.CreateScope(TestContext.Current.CancellationToken);
        var client = circuit.Resolve<ITestingCommandApi>();
        circuit.Resolve<CircuitAuthentication>().User = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.Equal(ResStatus.Unauthorized, (await circuit.Resolve<ITestingQueryApi>().Identity(new QueryIdentity(), TestContext.Current.CancellationToken)).Status);
        circuit.Resolve<CircuitAuthentication>().User = Principal("denied", allowed: false);
        Assert.Equal(ResStatus.Forbidden, (await circuit.Resolve<ITestingQueryApi>().Identity(new QueryIdentity(), TestContext.Current.CancellationToken)).Status);
        Assert.Equal("system", app.Services.GetRequiredService<CurrentPrincipalContext>().Principal!.Identity!.Name);
    }

    [Fact]
    public async Task Send_WhenHandlerFails_ShouldRollBackAndAllowNextOperation()
    {
        await using var app = await new InvocationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var circuit = app.CreateScope(TestContext.Current.CancellationToken);
        var client = circuit.Resolve<ITestingCommandApi>();
        var failed = await client.Store(new CommandStore("rollback", Fail: true), TestContext.Current.CancellationToken);
        Assert.Equal(ResStatus.InternalError, failed.Status);
        Assert.DoesNotContain("private database failure", failed.Message ?? string.Empty);
        Assert.False(((IDictionary<string, object?>?)failed.Metadata)?.ContainsKey("diagnostics") == true);
        Assert.Equal(ResStatus.Ok, (await client.Store(new CommandStore("committed"), TestContext.Current.CancellationToken)).Status);
        await app.VerifyAsync<OperationDbContext>(async (db, ct) =>
            Assert.Equal(new[] { "committed" }, await db.Entries.Select(entry => entry.Name).ToArrayAsync(ct)), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Send_WhenInvalidOrUnauthorized_ShouldMatchHttpAndNotPersist()
    {
        await using var app = await new InvocationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var circuit = app.CreateScope(TestContext.Current.CancellationToken);
        var local = await circuit.Resolve<ITestingCommandApi>().Store(new CommandStore(""), TestContext.Current.CancellationToken);
        Assert.Equal(ResStatus.BadRequest, local.Status);
        using var http = ((TestServer)app.Services.GetRequiredService<IServer>()).CreateClient();
        http.DefaultRequestHeaders.Add("X-Test-User", "test");
        using var invalid = await http.PostAsJsonAsync("/test/Testing/commands/Store", new CommandStore(""), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        http.DefaultRequestHeaders.Remove("X-Test-User");
        using var denied = await http.PostAsJsonAsync("/test/Testing/commands/Store", new CommandStore("denied"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await app.VerifyAsync<OperationDbContext>(async (db, ct) => Assert.Equal(0, await db.Entries.CountAsync(ct)), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Http_WhenSharedOperationIsPublished_ShouldBindAndUseHttpCallerThroughNestedLocalApi()
    {
        await using var app = await new InvocationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var http = ((TestServer)app.Services.GetRequiredService<IServer>()).CreateClient();
        http.DefaultRequestHeaders.Add("X-Test-User", "http-caller");
        using var response = await http.PostAsJsonAsync("/test/Testing/commands/Store", new CommandStore("http-owned"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<Res<OperationReceipt>>(TestContext.Current.CancellationToken);
        Assert.Equal("http-caller", receipt!.Data!.User);
        Assert.Equal(receipt.Data.ScopeId, receipt.Data.NestedScopeId);
        using var query = await http.PostAsJsonAsync("/test/Testing/queries/Identity", new QueryIdentity(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        var identity = await query.Content.ReadFromJsonAsync<Res<OperationReceipt>>(TestContext.Current.CancellationToken);
        Assert.Equal("http-caller", identity!.Data!.User);
        Assert.NotEqual(receipt.Data.ScopeId, identity.Data.ScopeId);
    }

    [Fact]
    public async Task Send_WhenCanceled_ShouldPropagateCancellationWithoutStartingWork()
    {
        await using var app = await new InvocationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var circuit = app.CreateScope(TestContext.Current.CancellationToken);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => circuit.Resolve<ITestingCommandApi>()
            .Store(new CommandStore("canceled"), canceled.Token));
        Assert.Empty(app.Services.GetRequiredService<ScopeLedger>().Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_WhenNestedCommandWrites_ShouldShareTransactionAndRollbackTogether(bool fail)
    {
        await using var app = await new InvocationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var circuit = app.CreateScope(TestContext.Current.CancellationToken);
        var result = await circuit.Resolve<ITestingCommandApi>().Store(new CommandStore("outer", fail) { CallNested = true }, TestContext.Current.CancellationToken);
        Assert.Equal(fail ? ResStatus.InternalError : ResStatus.Ok, result.Status);
        if (!fail) Assert.Equal(result.Data!.ScopeId, result.Data.NestedScopeId);
        await app.VerifyAsync<OperationDbContext>(async (db, ct) => Assert.Equal(fail ? 0 : 2, await db.Entries.CountAsync(ct)), TestContext.Current.CancellationToken);
    }

    internal static ClaimsPrincipal Principal(string name, bool allowed = true) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, name), new Claim("can-use", allowed ? "yes" : "no")], "test"));
}

internal sealed class InvocationFactory : MonicaTestApplicationFactory<CommandHandlerStore>
{
    protected override void ConfigureMonica(IMonicaBuilder builder)
    {
        builder.AddApplicationUI();
        builder.AddRpcClient().ConfigDomainInfoProvider(new TestingRpcDomains()).UseLocalTransport();
        builder.AddWebApi();
        builder.AddResultEnvelope(options => options.ExposeDiagnosticDetails = true);
        builder.AddRepository().AddRepositoryDbContext<OperationDbContext>((_, options) => options.UseSqlite("Data Source=unused"));
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.UseTestDatabase<OperationDbContext>();
        services.AddSingleton<ScopeLedger>();
        services.AddScoped<OperationLease>();
        services.AddScoped<CircuitAuthentication>();
        services.AddScoped<IApplicationCallerContext, TestCallerContext>();
        services.AddScoped<AuthenticationStateProvider>(provider => provider.GetRequiredService<CircuitAuthentication>());
        services.AddAuthorization(options => options.AddPolicy("use", policy => policy.RequireAuthenticatedUser().RequireClaim("can-use", "yes")));
        services.AddAuthentication(options => options.DefaultAuthenticateScheme = "test")
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("test", _ => { });
    }
}

public sealed class CircuitAuthentication : AuthenticationStateProvider
{
    public ClaimsPrincipal User { get; set; } = ApplicationInvocationTests.Principal("test");
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));
}

public sealed class ScopeLedger
{
    public ConcurrentDictionary<Guid, bool> Disposed { get; } = new();
    private int _arrivals;
    private readonly TaskCompletionSource _peers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _arrivals) == 2) _peers.TrySetResult();
        return _peers.Task.WaitAsync(cancellationToken);
    }
}

public sealed class OperationLease(ScopeLedger ledger) : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public void Dispose() => ledger.Disposed[Id] = true;
}

public sealed class OperationEntry : Entity<int>
{
    public string Name { get; set; } = "";
}

public sealed class OperationDbContext(DbContextOptions<OperationDbContext> options, ICachedServiceProvider provider)
    : RepositoryDbContext<OperationDbContext>(options, provider)
{
    public DbSet<OperationEntry> Entries => Set<OperationEntry>();
}

[Authorize(Policy = "use")]
public sealed class CommandHandlerStore(OperationDbContext db, OperationLease lease, ITestingQueryApi queries, ITestingCommandApi commands,
    ICurrentPrincipalAccessor principal) : ApplicationService<CommandStore, OperationReceipt>
{
    public override async Task<Res<OperationReceipt>> Handle(CommandStore request, CancellationToken cancellationToken)
    {
        var nested = await queries.Identity(new QueryIdentity(), cancellationToken);
        db.Entries.Add(new OperationEntry { Name = request.Name });
        await db.SaveChangesAsync(cancellationToken);
        if (request.CallNested)
        {
            var inner = await commands.Store(new CommandStore(request.Name + "-nested", request.Fail), cancellationToken);
            return Res.Ok(new OperationReceipt(lease.Id, principal.Principal.Identity?.Name, inner.Data!.ScopeId));
        }
        if (request.Fail) throw new InvalidOperationException("private database failure");
        return Res.Ok(new OperationReceipt(lease.Id, principal.Principal.Identity?.Name, nested.Data!.ScopeId));
    }
}

[Authorize(Policy = "use")]
public sealed class QueryHandlerIdentity(OperationLease lease, ICurrentPrincipalAccessor principal, ScopeLedger ledger)
    : ApplicationService<QueryIdentity, OperationReceipt>
{
    public override async Task<Res<OperationReceipt>> Handle(QueryIdentity request, CancellationToken cancellationToken)
    {
        if (request.WaitForPeer) await ledger.WaitAsync(cancellationToken);
        return Res.Ok(new OperationReceipt(lease.Id, principal.Principal.Identity?.Name));
    }
}

internal sealed class HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
        Request.Headers["X-Test-User"].FirstOrDefault() is { } user
            ? AuthenticateResult.Success(new AuthenticationTicket(ApplicationInvocationTests.Principal(user), Scheme.Name))
            : AuthenticateResult.NoResult());
}

internal sealed class TestCallerContext(CircuitAuthentication authentication) : IApplicationCallerContext
{
    public async ValueTask<ClaimsPrincipal> GetPrincipalAsync(CancellationToken cancellationToken) =>
        (await authentication.GetAuthenticationStateAsync().WaitAsync(cancellationToken)).User;
}

[Flags]
internal enum TestingDomains { None = 0, Testing = 1 }
internal sealed class TestingRpcDomains : IRpcClientDomainInfoProvider
{
    public object GetDependencyDomains() => TestingDomains.Testing;
    public RpcServiceDescriptor GetDomain(Enum domain) => throw new NotSupportedException("Local calls need no network destination.");
}
