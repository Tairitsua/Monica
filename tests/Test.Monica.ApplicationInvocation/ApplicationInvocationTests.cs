using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using global::ApplicationModel.Domain;
using global::ApplicationModel.Role;
using global::ApplicationInvocation.Fixtures;
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
using Monica.Invocation.Abstractions;
using Monica.Invocation.Models;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.ApplicationInvocation;

public sealed class ApplicationInvocationTests
{
    private static readonly ApiHttpEndpoint OrdinaryEndpoint = new("POST", "api/v1/Ordering/invocation/ordinary", false, true);
    private static readonly ApiHttpEndpoint RequiredEndpoint = new("POST", "api/v1/Ordering/invocation/constrained", false, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invoke_WhenSeveralFeaturesCustomizeHttpClients_ShouldKeepEveryHandlerInDeclarationOrder(bool authority)
    {
        await using var host = await new InvocationApplicationFactory(authority: authority, multipleHttpContributions: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        PrepareCaller(host);
        if (authority)
            host.Services.GetRequiredService<AuthorityChoice>().Destination = new ApplicationInvocationDestination("remote", "primary");
        await using var scope = host.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<IApplicationApiInvoker>()
            .InvokeAsync(new CommandConstrained(88), RequiredEndpoint, scope.CancellationToken);

        Assert.Equal(ResStatus.Ok, result.Status);
        Assert.Equal(501, result.Data);
        var transport = host.Services.GetRequiredService<RecordingTransport>();
        Assert.Equal(["first:request", "second:request", "second:response", "first:response"], transport.HandlerSteps.ToArray());
        var sent = Assert.Single(transport.Calls);
        Assert.StartsWith(authority ? "https://primary.test/remote/" : "https://invocation.test/remote/", sent.Uri);
        Assert.Equal("Bearer scenario-token", sent.Authorization);
    }

    [Fact]
    public async Task Invoke_WhenAnOrdinaryImplementationIsOmitted_ShouldReachItsOwnerWithoutLoadingTheHandler()
    {
        await using var host = await new InvocationApplicationFactory(remoteOnly: true, authority: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        PrepareCaller(host);
        await using var scope = host.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<IApplicationApiInvoker>().InvokeAsync(new QueryRemoteOnly(17), OrdinaryEndpoint, scope.CancellationToken);

        Assert.Equal(501, result.Data);
        var sent = Assert.Single(host.Services.GetRequiredService<RecordingTransport>().Calls);
        Assert.Equal("https://invocation.test/external/api/v1/Ordering/invocation/ordinary", sent.Uri);
        Assert.Equal("Bearer scenario-token", sent.Authorization);
        var policy = Assert.IsType<ApplicationInvocationAuthorityContext>(host.Services.GetRequiredService<AuthorityChoice>().Context);
        Assert.Null(policy.HandlerType);
        Assert.Equal("ordering.external", policy.Operation.RoleId);
        Assert.Equal(0, host.Services.GetRequiredService<InvocationLedger>().OrdinaryCalls);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "ApplicationInvocation.UnavailableRole");
    }

    [Fact]
    public async Task Invoke_WhenRemoteOnlyOperationFails_ShouldNotFallBackToLocalExecution()
    {
        await using var host = await new InvocationApplicationFactory(remoteOnly: true)
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
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new InvocationApplicationFactory(colocated: true, httpProvider: false, remoteOnly: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("HTTP provider", error.ToString());
    }

    [Fact]
    public async Task Invoke_WhenOrdinaryOwnerIsInactive_ShouldStayLocalWhileRequiredOperationUsesItsActualAppId()
    {
        await using var host = await new InvocationApplicationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
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
        Assert.Equal("https://invocation.test/remote/api/v1/Ordering/invocation/constrained", sent.Uri);
        Assert.Equal("Bearer scenario-token", sent.Authorization);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(88, body.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Invoke_WhenEverythingIsColocated_ShouldUseApplicationScopesWithoutAnHttpProvider()
    {
        await using var host = await new InvocationApplicationFactory(colocated: true, httpProvider: false)
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
        await using var host = await new InvocationApplicationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
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
        await using var host = await new InvocationApplicationFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
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
        await using var host = await new InvocationApplicationFactory(appId: "remote", authority: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        PrepareCaller(host);
        var choice = host.Services.GetRequiredService<AuthorityChoice>();
        choice.Destination = new ApplicationInvocationDestination("remote", "primary");
        var request = new QueryOrdinary("authority");
        await using var scope = host.CreateScope(TestContext.Current.CancellationToken);

        var result = await scope.Resolve<IApplicationApiInvoker>().InvokeAsync(request, OrdinaryEndpoint, scope.CancellationToken);

        Assert.Equal(501, result.Data);
        Assert.Same(request, choice.Context!.Request);
        Assert.Equal(typeof(OrdinaryApiHandler), choice.Context.HandlerType);
        Assert.Equal("ordering.invocation", choice.Context.Operation.RoleId);
        Assert.StartsWith("https://primary.test/remote/", Assert.Single(host.Services.GetRequiredService<RecordingTransport>().Calls).Uri);
        Assert.Equal(0, host.Services.GetRequiredService<InvocationLedger>().OrdinaryCalls);
    }

    [Theory]
    [InlineData("local", "primary", true)]
    [InlineData("unknown", "primary", false)]
    [InlineData("remote", "unknown", false)]
    public async Task Invoke_WhenAuthorityDestinationIsInvalid_ShouldRejectBeforeSending(string appId, string providerKey, bool constrained)
    {
        await using var host = await new InvocationApplicationFactory(authority: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<AuthorityChoice>().Destination = new ApplicationInvocationDestination(appId, providerKey);
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
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new InvocationApplicationFactory(httpProvider: false)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("HTTP provider", error.ToString());
    }

    [Theory]
    [InlineData("local", true)]
    [InlineData("remote", false)]
    public async Task Map_WhenOneControllerContainsSeveralRoleOwners_ShouldSelectActionsIndependently(string appId, bool workerActive)
    {
        await using var host = await new InvocationApplicationFactory(appId: appId, receivers: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();

        using var worker = await client.GetAsync("/invocation-selection/worker", TestContext.Current.CancellationToken);
        using var ordinary = await client.GetAsync("/invocation-selection/ordinary", TestContext.Current.CancellationToken);
        using var constrained = await client.GetAsync("/invocation-selection/constrained", TestContext.Current.CancellationToken);
        using var unrelated = await client.GetAsync("/invocation-selection/unrelated", TestContext.Current.CancellationToken);

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
        await using var host = await new InvocationApplicationFactory(appId: appId, receivers: true)
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

    private sealed class InvocationApplicationFactory(string appId = "local", bool colocated = false, bool httpProvider = true,
        bool authority = false, bool receivers = false, bool remoteOnly = false, bool multipleHttpContributions = false)
        : MonicaTestApplicationFactory<ApplicationInvocationTests>
    {
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies =>
            [typeof(ApplicationInvocationTests).Assembly, typeof(DomainDeclaration).Assembly, typeof(OrdinaryHandler).Assembly, typeof(OrdinaryApiHandler).Assembly];

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => options.AppId = appId);
            builder.AddApplicationModel(options =>
            {
                options.ManifestType = remoteOnly ? typeof(RemoteOnlyInvocationManifest) : typeof(InvocationScenarioManifest);
                options.Applications = new Dictionary<string, ApplicationPlacement>(StringComparer.Ordinal)
                {
                    ["local"] = new() { Roles = colocated ? ["ordering.worker", "ordering.invocation"] : ["ordering.worker"] },
                    ["remote"] = new() { Roles = colocated ? [] : ["ordering.invocation"] }
                };
                if (remoteOnly)
                    ((Dictionary<string, ApplicationPlacement>)options.Applications)["external"] = new() { Roles = ["ordering.external"] };
            });
            builder.AddApplication();
            builder.AddResultEnvelope().UseResultFieldNames(names => names.Status = "code");
            var applicationInvocation = builder.AddApplicationInvocation();
            if (multipleHttpContributions)
                builder.AddApiClient().ConfigHttpClientBuilder(client =>
                    client.AddHttpMessageHandler(provider => new OrderedHandler("first", provider.GetRequiredService<RecordingTransport>())));
            var apiClients = builder.AddApiClient().ConfigHttpClientBuilder(client =>
            {
                if (multipleHttpContributions)
                    client.AddHttpMessageHandler(provider => new OrderedHandler("second", provider.GetRequiredService<RecordingTransport>()));
                client.ConfigurePrimaryHttpMessageHandler(provider => new RecordingHandler(provider.GetRequiredService<RecordingTransport>()));
            });
            if (httpProvider) apiClients.ConfigHttpClientRegisterProvider<OrdinaryHttpProvider>();
            if (authority)
                applicationInvocation.UseAuthorityPolicy<FixedAuthorityPolicy>().ConfigAuthorityHttpClientProvider<PrimaryHttpProvider>("primary");
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

    private sealed class OrdinaryHttpProvider : IApiHttpClientRegisterProvider
    {
        public void ConfigureHttpClientFactoryOptions(HttpClientFactoryOptions options, string appId) =>
            options.HttpClientActions.Add(client => client.BaseAddress = new Uri($"https://invocation.test/{appId}/"));
    }

    private sealed class PrimaryHttpProvider : IApiHttpClientRegisterProvider
    {
        public void ConfigureHttpClientFactoryOptions(HttpClientFactoryOptions options, string appId) =>
            options.HttpClientActions.Add(client => client.BaseAddress = new Uri($"https://primary.test/{appId}/"));
    }

    private sealed class AuthorityChoice
    {
        public ApplicationInvocationDestination? Destination { get; set; }
        public ApplicationInvocationAuthorityContext? Context { get; set; }
    }

    private sealed class FixedAuthorityPolicy(AuthorityChoice choice) : IApplicationInvocationAuthorityPolicy
    {
        public ValueTask<ApplicationInvocationDestination?> ResolveAsync(ApplicationInvocationAuthorityContext context, CancellationToken cancellationToken)
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
        public ConcurrentQueue<string> HandlerSteps { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; set; }
        public bool WaitForCancellation { get; set; }
    }

    private sealed class OrderedHandler(string name, RecordingTransport transport) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            transport.HandlerSteps.Enqueue($"{name}:request");
            var response = await base.SendAsync(request, cancellationToken);
            transport.HandlerSteps.Enqueue($"{name}:response");
            return response;
        }
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

public sealed class RemoteOnlyInvocationManifest : IApplicationManifest
{
    private readonly IApplicationManifest _local = new InvocationScenarioManifest();
    public IReadOnlyList<string> LocalAssemblies => _local.LocalAssemblies;
    public IReadOnlyList<ApplicationDomainDefinition> Domains => _local.Domains;
    public IReadOnlyList<ApplicationRoleDefinition> Roles =>
        [.. _local.Roles, new("ordering.external", "Ordering", "ApplicationInvocation.UnavailableRole")];
    public IReadOnlyList<ApplicationOperationDefinition> Operations =>
        [.. _local.Operations, new(typeof(QueryRemoteOnly), "External.OrdinaryHandler", "ApplicationInvocation.UnavailableRole", "Ordering", "ordering.external", null)];
    public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
}

public sealed class InvocationScenarioManifest : IApplicationManifest
{
    public IReadOnlyList<ApplicationDomainDefinition> Domains =>
        [new("Ordering", typeof(DomainDeclaration).Assembly.GetName().Name!)];
    public IReadOnlyList<ApplicationRoleDefinition> Roles =>
    [
        new("ordering.worker", "Ordering", typeof(OrdinaryHandler).Assembly.GetName().Name!),
        new("ordering.invocation", "Ordering", typeof(OrdinaryApiHandler).Assembly.GetName().Name!)
    ];
    public IReadOnlyList<ApplicationOperationDefinition> Operations =>
    [
        Operation<OrdinaryQuery, OrdinaryHandler>("ordering.worker"),
        Operation<RoleQuery, RequiredHandler>("ordering.worker", "ordering.worker"),
        Operation<QueryOrdinary, OrdinaryApiHandler>("ordering.invocation"),
        Operation<CommandConstrained, ConstrainedApiHandler>("ordering.invocation", "ordering.invocation")
    ];
    public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    private static ApplicationOperationDefinition Operation<TRequest, THandler>(string role, string? required = null) =>
        new(typeof(TRequest), typeof(THandler).FullName!, typeof(THandler).Assembly.GetName().Name!, "Ordering", role, required);
}

[ApiController, Route("invocation-selection"), MediatedController]
public sealed class ApplicationApiReceiverController(IMediator mediator) : ControllerBase
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
