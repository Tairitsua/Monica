using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monica.Core.ApplicationRoles;

namespace Monica.HealthCheck.Services;

internal sealed class ApplicationRolesHealthCheck(IRoleHealthStateProvider roles) : IHealthCheck
{
    internal const string Name = "monica.application.roles";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = roles.GetSnapshot();
        return Task.FromResult(new HealthCheckResult(Evaluate(snapshot), "Current instance role readiness.",
            data: new Dictionary<string, object>
            {
                ["attentionCount"] = snapshot.Roles.Count(role => role.Status is RoleHealthStatus.Degraded or RoleHealthStatus.Faulted)
            }));
    }

    internal static HealthStatus Evaluate(RoleHealthSnapshot snapshot)
    {
        var active = snapshot.Roles.Where(role => role.IsActiveOnCurrentHost).ToArray();
        if (active.Length == 0) return HealthStatus.Healthy;
        if (active.All(role => role.Status is not (RoleHealthStatus.Healthy or RoleHealthStatus.Degraded))
            || active.Any(role => role.Status == RoleHealthStatus.Faulted && role.EffectiveCriticality == RoleCriticality.Required))
            return HealthStatus.Unhealthy;

        return active.Any(role => role.Status != RoleHealthStatus.Healthy) ? HealthStatus.Degraded : HealthStatus.Healthy;
    }
}
