using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ApplicationModel.Domain;
using ApplicationModel.Role;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.ApplicationModel.Models;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Annotations;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Core.ApplicationRoles;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.ApplicationModel;

public sealed class RoleStartupPreflightTests
{
    private const string WORKER_ROLE = "ordering.worker";
    private const string API_ROLE = "ordering.api";

    [Fact]
    public async Task OptionalWorkerFault_QuarantinesAllOwnedBoundariesAndKeepsTheActualValueUnavailable()
    {
        await using var app = await new Factory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var roles = app.Services.GetRequiredService<IRoleHealthStateProvider>();
        var snapshot = roles.GetSnapshot();
        Assert.Equal(2, snapshot.ApplicationCount);
        Assert.Equal("local-uat", snapshot.DeploymentScope);
        Assert.Equal(RoleHealthStatus.Healthy, snapshot.Roles.Single(role => role.RoleId == API_ROLE).Status);
        var worker = snapshot.Roles.Single(role => role.RoleId == WORKER_ROLE);
        Assert.Equal(RoleHealthStatus.Faulted, worker.Status);
        Assert.Equal(RoleCriticality.Optional, worker.EffectiveCriticality);
        var issue = Assert.Single(Assert.Single(worker.Faults).ConfigurationIssues);
        Assert.Equal(WorkerStartupOptions.KEY, issue.DefinitionKey);
        var validation = app.Services.GetRequiredService<IConfigurationRuntimeValidationService>();
        Assert.False(validation.GetReport().IsValid);
        Assert.True(validation.GetEnforcementReport().IsValid);
        Assert.Null(issue.CandidateObservedValidInThisInstance);
        Assert.False(roles.GetSnapshot(true).Roles.Single(role => role.RoleId == WORKER_ROLE)
            .Faults.Single().ConfigurationIssues.Single().CandidateObservedValidInThisInstance);

        var activation = app.Services.GetRequiredService<ITypeActivationPolicy>();
        Assert.Equal([API_ROLE], activation.ActiveWorkloadOwners);
        Assert.Equal([WORKER_ROLE], activation.SuspendedWorkloadOwners);
        TypeActivationKind[] held = [TypeActivationKind.Job, TypeActivationKind.ExternalSubscription,
            TypeActivationKind.HostedService, TypeActivationKind.DataChannel, TypeActivationKind.Seeder, TypeActivationKind.Outbox];
        foreach (var boundary in held)
            Assert.Equal(ActivationMode.Disabled, activation.GetActivation(typeof(object), boundary, typeof(Worker)));
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        var mediator = scope.Resolve<IMediator>();
        Assert.Equal("api", await mediator.Send(new ApiQuery(), scope.CancellationToken));
        var refusal = await Assert.ThrowsAsync<RoleUnavailableException>(() => mediator.Send(new OrdinaryQuery(), scope.CancellationToken));
        Assert.Equal(WORKER_ROLE, refusal.RoleId);
        var localOnly = await Assert.ThrowsAsync<RoleUnavailableException>(() => mediator.Send(new PrivateWorkerQuery(), scope.CancellationToken));
        Assert.Equal(WORKER_ROLE, localOnly.RoleId);

        app.Services.GetRequiredService<IConfiguration>()["WorkerStartup:Attempts"] = "4";
        var repairedCandidate = roles.GetSnapshot(true).Roles.Single(role => role.RoleId == WORKER_ROLE);
        Assert.Equal(RoleHealthStatus.Faulted, repairedCandidate.Status);
        Assert.True(repairedCandidate.Faults.Single().ConfigurationIssues.Single().CandidateObservedValidInThisInstance);
        Assert.Throws<StartupConfigurationUnavailableException>(() => app.Services.GetRequiredService<IOptions<WorkerStartupOptions>>().Value);
        var result = await app.Services.GetRequiredService<ConfigurationFacade>().GetDefinitionStateAsync(WorkerStartupOptions.KEY);
        Assert.NotNull(result.Data);
        Assert.Equal(ConfigurationStartupStatus.Faulted, result.Data.StartupStatus);
        var attempts = Assert.Single(result.Data.EffectiveValues);
        Assert.Null(attempts.DisplayValue);
        Assert.Null(attempts.EffectiveSource);
        Assert.Equal("4", attempts.StartupCandidate?.DisplayValue);
        Assert.True(attempts.StartupCandidate?.RequiresRestart);
    }

    [Fact]
    public async Task RepeatedEarlyHostReads_ShareTheValidatedInstanceWithPreflightAndDi()
    {
        var factory = new Factory(earlySharedRead: true);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var actual = app.Services.GetRequiredService<IOptions<SharedStartupOptions>>().Value;
        Assert.Same(factory.FirstRead, actual);
        Assert.Same(factory.SecondRead, actual);
        Assert.Equal(1, actual.ValidationCalls);
    }

    [Fact]
    public async Task ScopedEarlyRead_RequiresPlacementBeforeReturningAnInstance()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            new Factory(earlyScopedRead: true).CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("requires application-model preflight", error.ToString());
    }

    [Fact]
    public async Task ReadinessChecks_TrackDegradationAndRecoveryIndependentlyOfStartupFaults()
    {
        await using var app = await new Factory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var sink = app.Services.GetRequiredService<IRoleProbeStateSink>();
        var roles = app.Services.GetRequiredService<IRoleHealthStateProvider>();
        RoleHealthStatus Status(string id) => roles.GetSnapshot().Roles.Single(role => role.RoleId == id).Status;

        sink.Report(API_ROLE, "database", true);
        sink.Report(API_ROLE, "index", true);
        Assert.Equal(RoleHealthStatus.Degraded, Status(API_ROLE));
        sink.Report(API_ROLE, "database", false);
        Assert.Equal(RoleHealthStatus.Degraded, Status(API_ROLE));
        sink.Report(API_ROLE, "index", false);
        Assert.Equal(RoleHealthStatus.Healthy, Status(API_ROLE));
        sink.Report(WORKER_ROLE, "index", false);
        Assert.Equal(RoleHealthStatus.Faulted, Status(WORKER_ROLE));
    }

    [Fact]
    public async Task InactiveWorkerInputs_RemainUnboundAndUnenforcedOnApiHost()
    {
        await using var app = await new Factory(combined: false, appId: "api").CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var role = app.Services.GetRequiredService<IRoleHealthStateProvider>().GetSnapshot().Roles.Single(entry => entry.RoleId == WORKER_ROLE);
        Assert.Equal(RoleHealthStatus.Inactive, role.Status);
        Assert.Empty(role.Faults);
        var options = app.Services.GetRequiredService<IOptions<WorkerStartupOptions>>();
        var unavailable = Assert.Throws<StartupConfigurationUnavailableException>(() => options.Value);
        Assert.Equal(ConfigurationStartupStatus.NotRequiredOnHost, unavailable.Status);
        var result = await app.Services.GetRequiredService<ConfigurationFacade>().GetDefinitionStateAsync(WorkerStartupOptions.KEY);
        Assert.Equal(ConfigurationStartupStatus.NotRequiredOnHost, result.Data?.StartupStatus);
        Assert.False(result.Data?.StartupCandidate?.RequiresRestart);
        Assert.Equal(ConfigurationValidationCoverage.NotApplicable, result.Data?.StartupCandidate?.ValidationReport.Coverage);
    }

    [Fact]
    public async Task EmptyApplication_PublishesInactiveRolesAndLeavesScopedDefinitionsUnbound()
    {
        await using var app = await new Factory(combined: true, appId: "api")
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = app.Services.GetRequiredService<IRoleHealthStateProvider>().GetSnapshot(true);
        Assert.All(snapshot.Roles, role => Assert.Equal(RoleHealthStatus.Inactive, role.Status));
        Assert.Empty(app.Services.GetRequiredService<ITypeActivationPolicy>().ActiveWorkloadOwners!);
        Assert.Equal(ConfigurationStartupStatus.NotRequiredOnHost, Assert.Throws<StartupConfigurationUnavailableException>(
            () => app.Services.GetRequiredService<IOptions<WorkerStartupOptions>>().Value).Status);
    }

    [Fact]
    public async Task RoleFaults_ExcludeValidatorMessagesAndDictionaryKeysFromDiagnosticEvidence()
    {
        await using var app = await new Factory(unsafeValidator: true).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var role = app.Services.GetRequiredService<IRoleHealthStateProvider>().GetSnapshot(true).Roles.Single(entry => entry.RoleId == WORKER_ROLE);
        var diagnostic = Assert.Single(role.Faults.SelectMany(fault => fault.ConfigurationIssues),
            issue => issue.DefinitionKey == "test.nested-startup");
        Assert.DoesNotContain("private-key", diagnostic.LogicalPath);
        Assert.DoesNotContain("private-input", diagnostic.Problem);
        var report = app.Services.GetRequiredService<IConfigurationRuntimeValidationService>().GetReport("test.nested-startup");
        var issue = Assert.Single(report.Issues);
        Assert.DoesNotContain("private-key", issue.LogicalPath.ToCanonicalString());
        Assert.DoesNotContain("private-input", issue.Problem);
    }

    [Theory]
    [InlineData("Auto", "Required", true)]
    [InlineData("Auto", "Optional", false)]
    [InlineData("FailFast", "Optional", true)]
    public async Task FatalPolicy_BlocksHostBeforeStartup(string mode, string criticality, bool combined)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new Factory(mode, criticality, combined)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("worker-startup", error.ToString());
    }

    [Fact]
    public async Task UnscopedDefinition_FailsEvenWithIsolateAllAndAggregatesOtherStartupFailures()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new Factory("IsolateAll", invalidShared: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("worker-startup", error.ToString());
        Assert.Contains("shared-startup", error.ToString());
    }

    [Fact]
    public async Task IsolateAll_AllowsDiagnosticsOnASingleFaultedRequiredRole()
    {
        await using var app = await new Factory("IsolateAll", "Required", false)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(app.Services.GetRequiredService<IRoleHealthStateProvider>().IsRoleFaulted(WORKER_ROLE));
        Assert.Empty(app.Services.GetRequiredService<ITypeActivationPolicy>().ActiveWorkloadOwners!);
    }

    private sealed class Factory(string mode = "Auto", string criticality = "Optional", bool combined = true,
        string appId = "worker", bool invalidShared = false, bool unsafeValidator = false, bool earlySharedRead = false,
        bool earlyScopedRead = false)
        : MonicaTestApplicationFactory<ApiQuery>
    {
        internal SharedStartupOptions? FirstRead { get; private set; }
        internal SharedStartupOptions? SecondRead { get; private set; }

        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies =>
            [typeof(ApiQuery).Assembly, typeof(DomainDeclaration).Assembly, typeof(OrdinaryHandler).Assembly];

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            var input = new Dictionary<string, string?>
            {
                ["WorkerStartup:Attempts"] = "0",
                ["SharedStartup:Count"] = invalidShared ? "0" : "1",
                ["Application:RoleFaultPolicy:Mode"] = mode,
                ["Application:RoleFaultPolicy:RoleCriticalityOverrides:ordering.worker"] = criticality
            };
            if (unsafeValidator) input["NestedStartup:Values:private-key:Value"] = "private-input";
            builder.Configuration.AddInMemoryCollection(input);
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options =>
            {
                options.AppId = appId;
                options.DeploymentScope = "local-uat";
            });
            builder.AddApplicationModel(options =>
            {
                options.ManifestType = typeof(ScenarioManifest);
                options.Applications = new Dictionary<string, ApplicationPlacement>
                {
                    ["worker"] = new() { Roles = combined ? [API_ROLE, WORKER_ROLE] : [WORKER_ROLE] },
                    ["api"] = new() { Roles = combined ? [] : [API_ROLE] }
                };
            });
            builder.AddConfiguration();
            if (earlySharedRead)
            {
                FirstRead = builder.GetStartupOptions<SharedStartupOptions>();
                SecondRead = builder.GetStartupOptions<SharedStartupOptions>();
            }
            if (earlyScopedRead) builder.GetStartupOptions<WorkerStartupOptions>();
            builder.AddMediator();
            builder.AddDependencyInjection();
        }
    }
}

[Configuration("WorkerStartup", Kind = ConfigurationKind.Startup, DefinitionKey = KEY, RequiredByRoles = ["ordering.worker"])]
public sealed class WorkerStartupOptions
{
    internal const string KEY = "test.worker-startup";

    [Range(1, 10)]
    public int Attempts { get; set; } = 3;
}

[Configuration("SharedStartup", Kind = ConfigurationKind.Startup, DefinitionKey = "test.shared-startup")]
public sealed class SharedStartupOptions : IValidatableObject
{
    [Range(1, 10)]
    public int Count { get; set; } = 1;

    internal int ValidationCalls { get; private set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        ValidationCalls++;
        return [];
    }
}

[Configuration("NestedStartup", Kind = ConfigurationKind.Startup, DefinitionKey = "test.nested-startup", RequiredByRoles = ["ordering.worker"])]
public sealed class NestedStartupOptions
{
    public Dictionary<string, NestedRule> Values { get; set; } = [];

    public sealed class NestedRule : IValidatableObject
    {
        public string Value { get; set; } = "";

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
            [new ValidationResult($"Unexpected input {Value}", [nameof(Value)])];
    }
}
