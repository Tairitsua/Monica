using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using global::ApplicationModel.Domain;
using global::ApplicationModel.Role;
using global::ApplicationRpc.Fixtures;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Monica.Application.Abstractions;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.ApplicationModel.Models;
using Monica.Core.Execution.Mvc;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Rpc.Abstractions;
using Monica.Rpc.Models;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.ApplicationRpc;

public sealed class ApplicationRpcTests
{
    private static readonly RpcHttpEndpoint OrdinaryEndpoint = new("POST", "api/v1/Ordering/invocation/ordinary", false, true);
    private static readonly RpcHttpEndpoint RequiredEndpoint = new("POST", "api/v1/Ordering/invocation/constrained", false, true);

    [Fact]
    public async Task Invoke_WhenAnOrdinaryImplementationIsOmitted_ShouldReachItsOwnerWithoutLoadingTheHandler()
    {
        await using var host = await new RpcApplicationFactory(remoteOnly: true, authority: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        PrepareCaller(host);
        await using var scope = host.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<IApplicationApiInvoker>().InvokeAsync(new QueryRemoteOnly(17), OrdinaryEndpoint, scope.CancellationToken);

        Assert.Equal(501, result.Data);
        var sent = Assert.Single(host.Services.GetRequiredService<RecordingTransport>().Calls);
        Assert.Equal("https://rpc.test/external/api/v1/Ordering/invocation/ordinary", sent.Uri);
        Assert.Equal("Bearer scenario-token", sent.Authorization);
        var policy = Assert.IsType<ApplicationRpcAuthorityContext>(host.Services.GetRequiredService<AuthorityChoice>().Context);
        Assert.Null(policy.HandlerType);
        Assert.Equal("ordering.external", policy.Operation.RoleId);
        Assert.Equal(0, host.Services.GetRequiredService<InvocationLedger>().OrdinaryCalls);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "ApplicationRpc.UnavailableRole");
    }

    [Fact]
    public async Task Invoke_WhenRemoteOnlyOperationFails_ShouldNotFallBackToLocalExecution()
    {
        await using var host = await new RpcApplicationFactory(remoteOnly: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<RecordingTransport>().Fail = true;
        await using var scope = host.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<IApplicationApiInvoker>().InvokeAsync(new QueryRemoteOnly(17), OrdinaryEndpoint, scope.CancellationToken);

        Assert.Equal(ResStatus.ServiceUnavailable, result.Status);
        Assert.Single(host.Services.GetRequiredService<RecordingTransport>().Calls);
        Assert.Equal(0, host.Services.GetRequiredService<InvocationLedger>().OrdinaryCalls);
    }

    [Fact]
    public async Task Compose_WhenOnlyAnOrdinaryOperationIsRemoteWithoutProvider_ShouldFailBeforeStarting()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new RpcApplicationFactory(colocated: true, httpProvider: false, remoteOnly: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("HTTP provider", error.ToString());
    }

    [Fact]
    public async Task Invoke_WhenOrdinaryOwnerIsInactive_ShouldStayLocalWhileRequiredOperationUsesItsActualAppId()
    {
        await using var host = await new RpcApplicationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        PrepareCaller(host);
        await using var scope = host.CreateScope(TestContext.Current.CancellationToken);
        var invoker = scope.Resolve<IApplicationApiInvoker>();

        var ordinary = await invoker.InvokeAsync(new QueryOrdinary("local"), OrdinaryEndpoint, scope.CancellationToken);
        var constrained = await invoker.InvokeAsync(new CommandConstrained(88), RequiredEndpoint, scope.CancellationToken);

        Assert.Equal(41, ordinary.Data);
        Assert.Equal(501, constrained.Data);
        var ledger = host.Services.GetRequiredService<InvocationLedger>();
        Assert.Equal(1, ledger.OrdinaryCalls);
        Assert.Equal(0, ledger.ConstrainedCalls);
        var sent = Assert.Single(host.Services.GetRequiredService<RecordingTransport>().Calls);
        Assert.Equal("https://rpc.test/remote/api/v1/Ordering/invocation/constrained", sent.Uri);
        Assert.Equal("Bearer scenario-token", sent.Authorization);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(88, body.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Invoke_WhenEverythingIsColocated_ShouldUseApplicationScopesWithoutAnHttpProvider()
    {
        await using var host = await new RpcApplicationFactory(colocated: true, httpProvider: false)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = host.CreateScope(TestContext.Current.CancellationToken);
        var invoker = scope.Resolve<IApplicationApiInvoker>();

        Assert.Equal(41, (await invoker.InvokeAsync(new QueryOrdinary("one"), OrdinaryEndpoint, scope.CancellationToken)).Data);
        Assert.Equal(42, (await invoker.InvokeAsync(new CommandConstrained(3), RequiredEndpoint, scope.CancellationToken)).Data);
        Assert.Empty(host.Services.GetRequiredService<RecordingTransport>().Calls);
    }

    [Fact]
    public async Task Invoke_WhenRemoteExecutionFails_ShouldReturnFailureWithoutLocalFallback()
    {
        await using var host = await new RpcApplicationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        PrepareCaller(host);
        host.Services.GetRequiredService<RecordingTransport>().Fail = true;
        await using var scope = host.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<IApplicationApiInvoker>()
            .InvokeAsync(new CommandConstrained(9), RequiredEndpoint, scope.CancellationToken);

        Assert.Equal(ResStatus.ServiceUnavailable, result.Status);
        Assert.Single(host.Services.GetRequiredService<RecordingTransport>().Calls);
        Assert.Equal(0, host.Services.GetRequiredService<InvocationLedger>().ConstrainedCalls);
    }

    [Fact]
    public async Task Invoke_WhenCallerCancelsRemoteExchange_ShouldPropagateCancellationWithoutLocalExecution()
    {
        await using var host = await new RpcApplicationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        PrepareCaller(host);
        var transport = host.Services.GetRequiredService<RecordingTransport>();
        transport.WaitForCancellation = true;
        await using var scope = host.CreateScope(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken);
        var pending = scope.Resolve<IApplicationApiInvoker>().InvokeAsync(new CommandConstrained(9), RequiredEndpoint, cancellation.Token);
        await transport.Entered.Task.WaitAsync(scope.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, host.Services.GetRequiredService<InvocationLedger>().ConstrainedCalls);
    }

    [Fact]
    public async Task Invoke_WhenAuthorityChoosesAnotherScopeWithSameAppId_ShouldForceRemoteAndSupplyHandlerPolicyContext()
    {
        await using var host = await new RpcApplicationFactory(appId: "remote", authority: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        PrepareCaller(host);
        var choice = host.Services.GetRequiredService<AuthorityChoice>();
        choice.Destination = new ApplicationRpcDestination("remote", "primary");
        var request = new QueryOrdinary("authority");
        await using var scope = host.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<IApplicationApiInvoker>().InvokeAsync(request, OrdinaryEndpoint, scope.CancellationToken);

        Assert.Equal(501, result.Data);
        Assert.Same(request, choice.Context!.Request);
        Assert.Equal(typeof(OrdinaryRpcHandler), choice.Context.HandlerType);
        Assert.Equal("ordering.rpc", choice.Context.Operation.RoleId);
        Assert.StartsWith("https://primary.test/remote/", Assert.Single(host.Services.GetRequiredService<RecordingTransport>().Calls).Uri);
        Assert.Equal(0, host.Services.GetRequiredService<InvocationLedger>().OrdinaryCalls);
    }

    [Theory]
    [InlineData("local", "primary", true)]
    [InlineData("unknown", "primary", false)]
    [InlineData("remote", "unknown", false)]
    public async Task Invoke_WhenAuthorityDestinationIsInvalid_ShouldRejectBeforeSending(string appId, string providerKey, bool constrained)
    {
        await using var host = await new RpcApplicationFactory(authority: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<AuthorityChoice>().Destination = new ApplicationRpcDestination(appId, providerKey);
        await using var scope = host.CreateScope(TestContext.Current.CancellationToken);
        var invoker = scope.Resolve<IApplicationApiInvoker>();

        if (constrained)
            await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.InvokeAsync(new CommandConstrained(1), RequiredEndpoint, scope.CancellationToken));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.InvokeAsync(new QueryOrdinary("invalid"), OrdinaryEndpoint, scope.CancellationToken));
        Assert.Empty(host.Services.GetRequiredService<RecordingTransport>().Calls);
        Assert.Equal(0, host.Services.GetRequiredService<InvocationLedger>().OrdinaryCalls);
        Assert.Equal(0, host.Services.GetRequiredService<InvocationLedger>().ConstrainedCalls);
    }

    [Fact]
    public async Task Compose_WhenRequiredRoleIsRemoteWithoutProvider_ShouldFailBeforeHostStarts()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new RpcApplicationFactory(httpProvider: false)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("HTTP provider", error.ToString());
    }

    [Theory]
    [InlineData("local", true)]
    [InlineData("remote", false)]
    public async Task Map_WhenOneControllerContainsSeveralRoleOwners_ShouldSelectActionsIndependently(string appId, bool workerActive)
    {
        await using var host = await new RpcApplicationFactory(appId: appId, receivers: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();

        using var worker = await client.GetAsync("/rpc-selection/worker", TestContext.Current.CancellationToken);
        using var ordinary = await client.GetAsync("/rpc-selection/ordinary", TestContext.Current.CancellationToken);
        using var constrained = await client.GetAsync("/rpc-selection/constrained", TestContext.Current.CancellationToken);
        using var unrelated = await client.GetAsync("/rpc-selection/unrelated", TestContext.Current.CancellationToken);

        Assert.Equal(workerActive ? HttpStatusCode.OK : HttpStatusCode.NotFound, worker.StatusCode);
        Assert.Equal(workerActive ? HttpStatusCode.NotFound : HttpStatusCode.OK, ordinary.StatusCode);
        Assert.Equal(workerActive ? HttpStatusCode.NotFound : HttpStatusCode.OK, constrained.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unrelated.StatusCode);
    }

    [Theory]
    [InlineData("local", HttpStatusCode.NotFound)]
    [InlineData("remote", HttpStatusCode.OK)]
    public async Task Map_WhenCrudControllerBelongsToARole_ShouldUseItsDomainRouteOnlyOnTheOwningApplication(string appId, HttpStatusCode expected)
    {
        await using var host = await new RpcApplicationFactory(appId: appId, receivers: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();

        using var response = await client.GetAsync("/api/v1/Ordering/ledger", TestContext.Current.CancellationToken);
        using var wrongDomain = await client.GetAsync("/api/v1/Other/ledger", TestContext.Current.CancellationToken);
        using var manual = await client.GetAsync("/role-owned/manual", TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected, manual.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongDomain.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(43, body.RootElement.GetProperty("data").GetInt32());
        }
    }

    private static void PrepareCaller(MonicaTestApplication host)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer scenario-token";
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
    }

    private sealed class RpcApplicationFactory(string appId = "local", bool colocated = false, bool httpProvider = true,
        bool authority = false, bool receivers = false, bool remoteOnly = false) : MonicaTestApplicationFactory<ApplicationRpcTests>
    {
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies =>
            [typeof(ApplicationRpcTests).Assembly, typeof(DomainDeclaration).Assembly, typeof(OrdinaryHandler).Assembly, typeof(OrdinaryRpcHandler).Assembly];

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => options.AppId = appId);
            builder.AddApplicationModel(options =>
            {
                options.ManifestType = remoteOnly ? typeof(RemoteOnlyRpcManifest) : typeof(RpcScenarioManifest);
                options.Deployments = new Dictionary<string, ApplicationPlacement>(StringComparer.Ordinal)
                {
                    ["local"] = new() { Roles = colocated ? ["ordering.worker", "ordering.rpc"] : ["ordering.worker"] },
                    ["remote"] = new() { Roles = colocated ? [] : ["ordering.rpc"] }
                };
                if (remoteOnly)
                    ((Dictionary<string, ApplicationPlacement>)options.Deployments)["external"] = new() { Roles = ["ordering.external"] };
            });
            builder.AddApplication();
            builder.AddResultEnvelope().UseResultFieldNames(names => names.Status = "code");
            var applicationRpc = builder.AddApplicationRpc();
            var rpc = builder.AddRpcClient().ConfigHttpClientBuilder(client =>
                client.ConfigurePrimaryHttpMessageHandler(provider => new RecordingHandler(provider.GetRequiredService<RecordingTransport>())));
            if (httpProvider) rpc.ConfigHttpClientRegisterProvider<OrdinaryHttpProvider>();
            if (authority)
                applicationRpc.UseAuthorityPolicy<FixedAuthorityPolicy>().ConfigAuthorityHttpClientProvider<PrimaryHttpProvider>("primary");
            if (receivers) builder.AddAutoControllers(configureCrud: options => options.RoutePath = "api/v1/{domain}");
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.RemoveAll<IHttpClientFactory>();
            services.AddHttpClient();
            services.AddSingleton<RecordingTransport>();
            services.AddSingleton<AuthorityChoice>();
        }
    }

    private sealed class OrdinaryHttpProvider : IRpcHttpClientRegisterProvider
    {
        public void ConfigureHttpClientFactoryOptions(HttpClientFactoryOptions options, string appId) =>
            options.HttpClientActions.Add(client => client.BaseAddress = new Uri($"https://rpc.test/{appId}/"));
    }

    private sealed class PrimaryHttpProvider : IRpcHttpClientRegisterProvider
    {
        public void ConfigureHttpClientFactoryOptions(HttpClientFactoryOptions options, string appId) =>
            options.HttpClientActions.Add(client => client.BaseAddress = new Uri($"https://primary.test/{appId}/"));
    }

    private sealed class AuthorityChoice
    {
        public ApplicationRpcDestination? Destination { get; set; }
        public ApplicationRpcAuthorityContext? Context { get; set; }
    }

    private sealed class FixedAuthorityPolicy(AuthorityChoice choice) : IApplicationRpcAuthorityPolicy
    {
        public ValueTask<ApplicationRpcDestination?> ResolveAsync(ApplicationRpcAuthorityContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            choice.Context = context;
            return ValueTask.FromResult(choice.Destination);
        }
    }

    private sealed record SentRequest(string Uri, string Body, string? Authorization);

    private sealed class RecordingTransport
    {
        public ConcurrentQueue<SentRequest> Calls { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; set; }
        public bool WaitForCancellation { get; set; }
    }

    private sealed class RecordingHandler(RecordingTransport transport) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            transport.Calls.Enqueue(new SentRequest(request.RequestUri!.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.ToString()));
            transport.Entered.TrySetResult();
            if (transport.WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (transport.Fail) throw new HttpRequestException("The selected role owner is unavailable.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":200,"message":"","data":501}""", Encoding.UTF8, "application/json")
            };
        }
    }
}

public sealed record QueryRemoteOnly(int Value) : IResultRequest<int>;

public sealed class RemoteOnlyRpcManifest : IApplicationManifest
{
    private readonly IApplicationManifest _local = new RpcScenarioManifest();
    public IReadOnlyList<string> LocalAssemblies => _local.LocalAssemblies;
    public IReadOnlyList<ApplicationDomainDefinition> Domains => _local.Domains;
    public IReadOnlyList<ApplicationRoleDefinition> Roles =>
        [.. _local.Roles, new("ordering.external", "Ordering", "ApplicationRpc.UnavailableRole")];
    public IReadOnlyList<ApplicationOperationDefinition> Operations =>
        [.. _local.Operations, new(typeof(QueryRemoteOnly), "External.OrdinaryHandler", "ApplicationRpc.UnavailableRole", "Ordering", "ordering.external", null)];
    public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
}

public sealed class RpcScenarioManifest : IApplicationManifest
{
    public IReadOnlyList<ApplicationDomainDefinition> Domains =>
        [new("Ordering", typeof(DomainDeclaration).Assembly.GetName().Name!)];
    public IReadOnlyList<ApplicationRoleDefinition> Roles =>
    [
        new("ordering.worker", "Ordering", typeof(OrdinaryHandler).Assembly.GetName().Name!),
        new("ordering.rpc", "Ordering", typeof(OrdinaryRpcHandler).Assembly.GetName().Name!)
    ];
    public IReadOnlyList<ApplicationOperationDefinition> Operations =>
    [
        Operation<OrdinaryQuery, OrdinaryHandler>("ordering.worker"),
        Operation<RoleQuery, RequiredHandler>("ordering.worker", "ordering.worker"),
        Operation<QueryOrdinary, OrdinaryRpcHandler>("ordering.rpc"),
        Operation<CommandConstrained, ConstrainedRpcHandler>("ordering.rpc", "ordering.rpc")
    ];
    public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    private static ApplicationOperationDefinition Operation<TRequest, THandler>(string role, string? required = null) =>
        new(typeof(TRequest), typeof(THandler).FullName!, typeof(THandler).Assembly.GetName().Name!, "Ordering", role, required);
}

[ApiController, Route("rpc-selection"), MediatedController]
public sealed class ApplicationRpcReceiverController(IMediator mediator) : ControllerBase
{
    [HttpGet("worker"), ApplicationApiReceiver(typeof(OrdinaryQuery))]
    public Task<string> Worker(CancellationToken cancellationToken) => mediator.Send(new OrdinaryQuery(), cancellationToken);

    [HttpGet("ordinary"), ApplicationApiReceiver(typeof(QueryOrdinary))]
    public Task<Res<int>> Ordinary(CancellationToken cancellationToken) => mediator.Send(new QueryOrdinary("http"), cancellationToken);

    [HttpGet("constrained"), ApplicationApiReceiver(typeof(CommandConstrained))]
    public Task<Res<int>> Constrained(CancellationToken cancellationToken) => mediator.Send(new CommandConstrained(1), cancellationToken);

    [HttpGet("unrelated")]
    public string Unrelated() => "available";
}
