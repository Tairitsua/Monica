using System.Reflection;
using ApplicationModel.Domain;
using ApplicationModel.Role;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.ApplicationModel.Models;
using Monica.Core;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Providers;
using Xunit;

[assembly: ApplicationRole("ordering.api", DomainId.Ordering)]

namespace Test.Monica.ApplicationModel;

public sealed class ApplicationModelTests
{
    [Fact]
    public async Task ManualPolicy_ShouldKeepPassiveAndPlacedRequestsWhileHoldingAutonomousWork()
    {
        await using var app = await new ModelFactory("worker", autonomousWork: false).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var policy = app.Services.GetRequiredService<ITypeActivationPolicy>();
        Assert.Equal(ActivationMode.Manual, policy.GetActivation(typeof(Worker), TypeActivationKind.Job));
        Assert.Equal(ActivationMode.Automatic, policy.GetActivation(typeof(Worker), TypeActivationKind.Service));
        Assert.Equal(ActivationMode.Automatic, policy.GetActivation(typeof(Worker), TypeActivationKind.Endpoint));
        Assert.Equal(ActivationMode.Manual, policy.GetActivation(typeof(object), TypeActivationKind.Outbox, typeof(Worker)));
        Assert.Equal("ordering.worker", policy.GetWorkloadOwner(typeof(object), typeof(Worker)));
        Assert.Throws<InvalidOperationException>(() => policy.GetActivation(typeof(Worker), TypeActivationKind.HostedService, typeof(ApiQuery)));
        Assert.Throws<InvalidOperationException>(() => policy.GetWorkloadOwner(typeof(Worker), typeof(ApiQuery)));
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), service => service is Worker);
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        Assert.Equal("required", await scope.Resolve<IMediator>().Send(new RoleQuery(), TestContext.Current.CancellationToken));
        Assert.Contains(app.Services.GetRequiredService<MonicaApplication>().ActivationResources,
            resource => resource.ImplementationType == typeof(Worker) && resource.Mode == ActivationMode.Manual);
    }

    [Fact]
    public async Task ProcessLocal_ShouldIgnorePlacementOnlyForActualBoundedInitializer()
    {
        await using var app = await new ModelFactory("api", autonomousWork: false).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var policy = app.Services.GetRequiredService<ITypeActivationPolicy>();
        Assert.Equal(ActivationMode.Automatic, policy.GetActivation(typeof(WorkerLocalInitialization), TypeActivationKind.Seeder));
        Assert.Equal(ActivationMode.Automatic, policy.GetActivation(typeof(DomainLocalInitialization), TypeActivationKind.Seeder));
        Assert.Equal(ActivationMode.Disabled, policy.GetActivation(typeof(WorkerLocalInitialization), TypeActivationKind.Job));
        Assert.Equal(ActivationMode.Disabled, policy.GetActivation(typeof(object), TypeActivationKind.HostedService, typeof(WorkerLocalInitialization)));
        Assert.Equal(ActivationMode.Automatic, policy.GetActivation(typeof(DomainDeclaration), TypeActivationKind.Service));
        Assert.Contains("BackgroundRole", Assert.Throws<InvalidOperationException>(() =>
            policy.GetActivation(typeof(DomainDeclaration), TypeActivationKind.Outbox)).Message);
    }

    [Fact]
    public async Task ServiceDiscovery_ReusesSelectedRolesAndDetachesRegistrationEvidence()
    {
        await using var app = await new ModelFactory("api").CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var model = app.Services.GetRequiredService<IApplicationModel>();
        var info = new DefaultServiceDiscoveryClientInfo(Options.Create(new ModuleServiceDiscoveryOption()),
            app.Services.GetRequiredService<MonicaApplication>().Application, applicationModel: model);
        var registration = info.GetServiceStatus();
        Assert.Equal("api", registration.AppId);
        Assert.Equal(new[] { "ordering.api" }, registration.RoleIds);
        Assert.Equal(new[] { "Ordering" }, registration.DomainNames);
        var copy = ServiceDiscoverySnapshot.Copy(registration);
        registration.RoleIds.Clear();
        registration.DomainNames.Clear();
        Assert.Single(copy.RoleIds);
        Assert.Single(info.GetServiceStatus().DomainNames);
        var catalog = new DefaultServiceDiscoveryCatalogProvider(applicationModel: model);
        Assert.Equal(new[] { "api", "worker" }, (await catalog.GetPreloadedServicesAsync()).Select(service => service.AppId));
        Assert.Equal("Ordering", Assert.Single(app.Services.GetRequiredService<global::Monica.Core.Domains.Abstractions.IDomainCatalog>().Domains).Key);
        Assert.Equal("api", registration.AppName);
    }

    [Fact]
    public async Task InactiveRole_RemainsLocallyCallableButCannotExecuteConstrainedRequests()
    {
        await using var app = await new ModelFactory("api").CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        var model = app.Services.GetRequiredService<IApplicationModel>();
        var mediator = scope.Resolve<IMediator>();
        Assert.Equal("ordinary", await mediator.Send(new OrdinaryQuery(), TestContext.Current.CancellationToken));
        Assert.Equal("api", await mediator.Send(new ApiQuery(), TestContext.Current.CancellationToken));
        Assert.False(model.IsRoleActive("ordering.worker"));
        Assert.Equal("worker", model.ResolveRoleAppId("ordering.worker"));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new RoleQuery(), TestContext.Current.CancellationToken));
        Assert.Contains("requires role 'ordering.worker'", failure.Message);
        Assert.Null(scope.Resolve<IServiceProvider>().GetService<RequiredHandler>());
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), service => service is Worker);
    }

    [Fact]
    public async Task SelectedRole_EnablesItsConstrainedHandlerAndUsesItsLogicalOwner()
    {
        await using var app = await new ModelFactory("worker").CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        Assert.Equal("required", await scope.Resolve<IMediator>().Send(new RoleQuery(), TestContext.Current.CancellationToken));
        var activation = app.Services.GetRequiredService<ITypeActivationPolicy>();
        Assert.Equal("ordering.worker", activation.GetWorkloadOwner(typeof(Worker)));
        Assert.Equal(ActivationMode.Automatic, activation.GetActivation(typeof(Worker), TypeActivationKind.Job));
        Assert.Contains(app.Services.GetServices<IHostedService>(), service => service is Worker);
    }

    [Fact]
    public async Task Placement_IsCopiedAndCannotChangeAnExistingHost()
    {
        var placements = Placements();
        await using var app = await new ModelFactory("api", placements).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        placements["api"].Roles![0] = "ordering.worker";
        placements.Remove("worker");
        var model = app.Services.GetRequiredService<IApplicationModel>();
        Assert.True(model.IsRoleActive("ordering.api"));
        Assert.False(model.IsRoleActive("ordering.worker"));
        Assert.Throws<InvalidOperationException>(() => app.Services.GetRequiredService<MonicaApplication>()
            .SetTypeActivationPolicy(app.Services.GetRequiredService<ITypeActivationPolicy>()));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task InvalidPlacement_FailsDuringComposition(string defect)
    {
        var placements = Placements();
        switch (defect)
        {
            case "unknown": placements["worker"].Roles = ["unknown"]; break;
            case "missing": placements["worker"].Roles = []; break;
            case "duplicate": placements["worker"].Roles = ["ordering.api"]; break;
        }
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new ModelFactory("api", placements).CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        var expected = defect switch
        {
            "unknown" => "unknown role 'unknown'",
            "missing" => "has no owner",
            _ => "assigned more than once"
        };
        Assert.Contains(expected, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncompleteDiscovery_RejectsACompiledRoleExcludedByHostFiltering()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new ModelFactory("api", excludeWorker: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("excluded", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deployment_DoesNotInheritDottedRoles()
    {
        var deployment = ApplicationDeployment.Create("api", Placements());
        Assert.False(deployment.IsRoleActive("ordering"));
        Assert.False(deployment.IsRoleActive("Ordering.Api"));
    }

    private static Dictionary<string, ApplicationPlacement> Placements() => new(StringComparer.Ordinal)
    {
        ["api"] = new() { Roles = ["ordering.api"] },
        ["worker"] = new() { Roles = ["ordering.worker"] }
    };

    private sealed class ModelFactory(string appId, Dictionary<string, ApplicationPlacement>? placements = null, bool excludeWorker = false, bool autonomousWork = true)
        : MonicaTestApplicationFactory<ApiQuery>
    {
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => excludeWorker
            ? [typeof(ApiQuery).Assembly, typeof(DomainDeclaration).Assembly]
            : [typeof(ApiQuery).Assembly, typeof(DomainDeclaration).Assembly, typeof(OrdinaryHandler).Assembly];
        protected override void ConfigureHost(WebApplicationBuilder builder) => builder.Services.AddHostedService<Worker>();
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => { options.AppId = appId; options.AutonomousWork = autonomousWork; });
            builder.AddApplicationModel(options =>
            {
                options.ManifestType = typeof(ScenarioManifest);
                options.Deployments = placements ?? Placements();
            });
            builder.AddMediator();
            builder.AddDependencyInjection();
        }
    }
}

public sealed record ApiQuery : IRequest<string>;
public sealed class ApiHandler : IRequestHandler<ApiQuery, string>
{
    public Task<string> Handle(ApiQuery request, CancellationToken cancellationToken) => Task.FromResult("api");
}

public sealed class ScenarioManifest : IApplicationManifest
{
    public IReadOnlyList<ApplicationDomainDefinition> Domains { get; } = [new("Ordering", typeof(DomainDeclaration).Assembly.GetName().Name!)];
    public IReadOnlyList<ApplicationRoleDefinition> Roles { get; } =
    [
        new("ordering.api", "Ordering", typeof(ApiHandler).Assembly.GetName().Name!),
        new("ordering.worker", "Ordering", typeof(OrdinaryHandler).Assembly.GetName().Name!)
    ];
    public IReadOnlyList<ApplicationOperationDefinition> Operations { get; } =
    [
        Operation<ApiQuery, ApiHandler>("ordering.api"),
        Operation<OrdinaryQuery, OrdinaryHandler>("ordering.worker"),
        Operation<RoleQuery, RequiredHandler>("ordering.worker", "ordering.worker")
    ];
    public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    private static ApplicationOperationDefinition Operation<TRequest, THandler>(string role, string? required = null) =>
        new(typeof(TRequest), typeof(THandler).FullName!, typeof(THandler).Assembly.GetName().Name!, "Ordering", role, required);
}
