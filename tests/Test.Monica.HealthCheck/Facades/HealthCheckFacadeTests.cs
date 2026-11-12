using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Results;
using Monica.HealthCheck.Facades;
using Monica.HealthCheck.Models;
using Monica.Modules;
using Xunit;

namespace Test.Monica.HealthCheck.Facades;

public sealed class HealthCheckFacadeTests
{
    [Fact]
    public async Task GetSnapshotAsync_WhenCheckReturnsDiagnostics_ShouldSanitizeAndBoundData()
    {
        var diagnosticData = Enumerable.Range(0, 40)
            .ToDictionary(index => $"{index + 2:D2}-value", index => (object)index);
        diagnosticData["00-authToken"] = "must-not-leak";
        diagnosticData["01-long"] = new string('x', 3_000);

        await using var application = await StartApplicationAsync(services =>
            services.AddHealthChecks()
                .AddCheck(
                    "test.degraded",
                    () => new HealthCheckResult(
                        HealthStatus.Degraded,
                        "Synthetic degradation",
                        new InvalidOperationException("Synthetic failure"),
                        diagnosticData),
                    tags: [global::Monica.HealthCheck.HealthCheckTags.Ready]));

        var facade = application.Services.GetRequiredService<HealthCheckFacade>();
        var result = await facade.GetSnapshotAsync(
            HealthCheckScope.Readiness,
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Data.Should().NotBeNull();
        result.Data!.Scope.Should().Be(HealthCheckScope.Readiness);
        result.Data.Status.Should().Be(HealthCheckState.Degraded);
        result.Data.HealthyCount.Should().Be(1);
        result.Data.DegradedCount.Should().Be(1);
        result.Data.UnhealthyCount.Should().Be(0);

        var entry = result.Data.Entries.Should()
            .ContainSingle(snapshot => snapshot.Name == "test.degraded")
            .Which;
        entry.Data.Should().HaveCount(32);
        entry.Data["00-authToken"].Should().Be("[redacted]");
        entry.Data["01-long"].Should().HaveLength(2 * 1024);
        entry.Error.Should().NotBeNull();
        entry.Error!.Type.Should().Be(typeof(InvalidOperationException).FullName);
        entry.Error.Message.Should().Be("Synthetic failure");
    }

    private static async Task<WebApplication> StartApplicationAsync(
        Action<IServiceCollection> configureServices)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options
                .ExcludeDefault()
                .Add(typeof(ModuleHealthCheck).Assembly));
            monica.AddHealthCheck();
        });
        configureServices(builder.Services);

        var application = builder.Build();
        application.UseMonica();
        application.MapMonica();
        await application.StartAsync(TestContext.Current.CancellationToken);
        return application;
    }
}
