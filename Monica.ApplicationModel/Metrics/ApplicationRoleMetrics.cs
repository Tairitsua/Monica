using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Monica.Core.ApplicationRoles;

namespace Monica.ApplicationModel.Metrics;

/// <summary>Names exported by application-model hosts for role isolation telemetry.</summary>
public static class ApplicationRoleMetricNames
{
    /// <summary>The meter selected by the default Monica OpenTelemetry pattern.</summary>
    public const string MeterName = "Monica.ApplicationModel";
    /// <summary>Observable gauge for the current state of each assigned role.</summary>
    public const string State = "monica.application.role.state";
    /// <summary>Counter incremented only when a local or HTTP admission gate rejects a request.</summary>
    public const string QuarantinedRequests = "monica.application.role.quarantined_requests";
}

/// <summary>Publishes this host's assigned role states and actual admission refusals.</summary>
internal sealed class ApplicationRoleMetrics : IRoleQuarantineRequestMetrics, IHostedService
{
    private readonly Counter<long> _rejections;

    public ApplicationRoleMetrics(IMeterFactory factory, IRoleHealthStateProvider health)
    {
        var meter = factory.Create(ApplicationRoleMetricNames.MeterName);
        meter.CreateObservableGauge(ApplicationRoleMetricNames.State, () => ObserveRoles(health),
            description: "Local role state (0 healthy, 1 degraded, 2 faulted).");
        _rejections = meter.CreateCounter<long>(ApplicationRoleMetricNames.QuarantinedRequests, "{request}",
            "Requests rejected because the local role is quarantined.");
    }

    private static IEnumerable<Measurement<int>> ObserveRoles(IRoleHealthStateProvider health)
    {
        foreach (var role in health.GetSnapshot().Roles)
        {
            var value = role.Status switch
            {
                RoleHealthStatus.Healthy => 0,
                RoleHealthStatus.Degraded => 1,
                RoleHealthStatus.Faulted => 2,
                _ => -1
            };
            if (value < 0) continue;
            yield return new Measurement<int>(value, new("role_id", role.RoleId),
                new("criticality", role.EffectiveCriticality.ToString()),
                new("fault_stage", role.Faults.LastOrDefault()?.Stage.ToString() ?? "none"));
        }
    }

    public void RecordHttpRejection(string roleId) => RecordRejection(roleId, "http");
    public void RecordLocalRejection(string roleId) => RecordRejection(roleId, "local");

    private void RecordRejection(string roleId, string transport)
    {
        try { _rejections.Add(1, new("role_id", roleId), new("transport", transport)); }
        catch (Exception) { /* A listener must not replace the caller's 503 result. */ }
    }

    // Host activation publishes the observable instruments even when no requests arrive.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
