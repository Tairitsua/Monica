using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monica.Core.ApplicationRoles;
using Monica.Core.Modularity.Extensions;
using Monica.HealthCheck.Extensions;
using Monica.Modules;
using Xunit;

namespace Test.Monica.HealthCheck.Modules;

public sealed class RoleReadinessTests
{
    [Fact]
    public async Task OptionalReadiness_CapsRawResult_WithoutChangingLivenessOrUnownedChecks()
    {
        var state = new ProbeFixture();
        await using var app = await StartAsync(state);
        using var client = app.GetTestClient();
        var checks = app.Services.GetRequiredService<HealthCheckService>();
        var token = TestContext.Current.CancellationToken;

        var report = await checks.CheckHealthAsync(r => r.Tags.Contains("ready"), token);
        var entry = report.Entries["test.optional"];
        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Equal("Unhealthy", entry.Data["rawStatus"]);
        Assert.True(state.Degraded);
        Assert.Equal(1, state.Created);
        Assert.Equal(1, state.Disposed);
        using var live = await client.GetAsync("/alive", token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, live.StatusCode);
        state.InfrastructureHealthy = false;
        using var ready = await client.GetAsync("/health", token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        state.InfrastructureHealthy = true;
        state.Healthy = true;
        report = await checks.CheckHealthAsync(r => r.Name == "test.optional", token);
        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.False(state.Degraded);
        using var recovered = await client.GetAsync("/health", token);
        Assert.Equal("Healthy", await recovered.Content.ReadAsStringAsync(token));
        Assert.Equal(state.Created, state.Disposed);
    }

    [Theory]
    [InlineData(RoleCriticality.Optional, HealthStatus.Degraded)]
    [InlineData(RoleCriticality.Required, HealthStatus.Unhealthy)]
    public async Task QuarantinedCheck_IsNotConstructed(RoleCriticality criticality, HealthStatus expected)
    {
        var state = new ProbeFixture { Faulted = true, Criticality = criticality };
        await using var app = await StartAsync(state);
        var report = await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            r => r.Tags.Contains("ready"), TestContext.Current.CancellationToken);
        Assert.Equal(expected, report.Status);
        Assert.Equal(true, report.Entries["test.optional"].Data["quarantined"]);
        Assert.Equal(0, state.Created);
    }

    [Fact]
    public async Task OptionalTimeout_ReportsRawUnhealthy_AndCallerCancellationPropagates()
    {
        var state = new ProbeFixture { Wait = true };
        await using var app = await StartAsync(state);
        var checks = app.Services.GetRequiredService<HealthCheckService>();
        var report = await checks.CheckHealthAsync(r => r.Name == "test.optional", TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Equal("Unhealthy", report.Entries["test.optional"].Data["rawStatus"]);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checks.CheckHealthAsync(r => r.Name == "test.optional", cancel.Token));
    }

    [Theory]
    [InlineData(false, HealthStatus.Healthy)]
    [InlineData(true, HealthStatus.Unhealthy)]
    public async Task Aggregate_AcceptsEmptyPlacement_BlocksAnEntirelyFaultedHost(bool active, HealthStatus expected)
    {
        var state = new ProbeFixture { Faulted = true, Active = active, Sibling = false };
        await using var app = await StartAsync(state);
        var report = await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            r => r.Tags.Contains("ready"), TestContext.Current.CancellationToken);
        Assert.Equal(expected, report.Entries["monica.application.roles"].Status);
        Assert.Equal(active, report.Entries.ContainsKey("test.optional"));
    }

    private static async Task<WebApplication> StartAsync(ProbeFixture state)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton<IRoleHealthStateProvider>(state);
        builder.Services.AddSingleton<IRoleProbeStateSink>(state);
        builder.Services.AddScoped<OwnedCheck>();
        builder.Services.AddHealthChecks()
            .AddMonicaReadinessCheck<OwnedCheck>("test.optional", tags: ["live"], timeout: TimeSpan.FromMilliseconds(75))
            .AddCheck("test.infrastructure", () => state.InfrastructureHealthy ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy(), tags: ["ready"]);
        builder.AddMonica(m =>
        {
            m.ConfigureTypeDiscovery(d => d.ExcludeDefault().Add(typeof(ModuleHealthCheck).Assembly));
            m.AddHealthCheck();
        });
        var app = builder.Build();
        app.UseMonica();
        app.MapMonica();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class OwnedCheck : IHealthCheck, IAsyncDisposable
    {
        private readonly ProbeFixture _state;

        public OwnedCheck(ProbeFixture state)
        {
            _state = state;
            Interlocked.Increment(ref _state.Created);
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (_state.Wait) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return _state.Healthy ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _state.Disposed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProbeFixture : IRoleHealthStateProvider, IRoleProbeStateSink
    {
        public int Created, Disposed;
        public bool Active = true, Sibling = true, InfrastructureHealthy = true, Faulted, Healthy, Wait, Degraded;
        public RoleCriticality Criticality = RoleCriticality.Optional;

        public string? GetRoleId(Type implementationType, Type? registrationOwner = null) => implementationType == typeof(OwnedCheck) ? "optional" : null;
        public bool IsRoleFaulted(string roleId) => roleId == "optional" && Faulted;
        public void Report(string roleId, string registrationName, bool degraded) => Degraded = degraded;

        public RoleHealthSnapshot GetSnapshot(bool includeCandidateValidation = false)
        {
            var status = !Active ? RoleHealthStatus.Inactive : Faulted ? RoleHealthStatus.Faulted : Degraded ? RoleHealthStatus.Degraded : RoleHealthStatus.Healthy;
            var roles = new List<RoleHealthEntrySnapshot>
            {
                new("optional", "domain", "optional", "app", Criticality, null, status, DateTimeOffset.UnixEpoch, [])
            };
            if (Sibling) roles.Add(new("core", "domain", "core", "app", RoleCriticality.Required, null, RoleHealthStatus.Healthy, DateTimeOffset.UnixEpoch, []));
            return new("app", "Application", "test", RoleFaultPolicyMode.Auto, true, roles);
        }
    }
}
