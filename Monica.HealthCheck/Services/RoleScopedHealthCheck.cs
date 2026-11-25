using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monica.Core.ApplicationRoles;

namespace Monica.HealthCheck.Services;

/// <summary>Defers even factory activation until quarantine has been checked within the native probe's own service scope.</summary>
internal sealed class RoleScopedHealthCheck(IServiceProvider services, IRoleHealthStateProvider roles,
    IRoleProbeStateSink? probes, string roleId, HealthCheckRegistration original, bool cap) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var role = roles.GetSnapshot().Roles.First(entry => entry.RoleId == roleId);
        if (role.Status == RoleHealthStatus.Faulted)
        {
            var status = role.EffectiveCriticality == RoleCriticality.Required ? HealthStatus.Unhealthy : HealthStatus.Degraded;
            return new HealthCheckResult(status, "The assigned role is quarantined on this instance.",
                data: new Dictionary<string, object> { ["roleId"] = roleId, ["quarantined"] = true });
        }

        HealthCheckResult raw;
        using var timeout = original.Timeout > TimeSpan.Zero ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        timeout?.CancelAfter(original.Timeout);
        try
        {
            raw = await original.Factory(services).CheckHealthAsync(new HealthCheckContext { Registration = original }, timeout?.Token ?? cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException error) when (timeout?.IsCancellationRequested == true)
        {
            raw = new HealthCheckResult(original.FailureStatus, "The role readiness check timed out.", error);
        }
        catch (Exception error)
        {
            raw = new HealthCheckResult(original.FailureStatus, error.Message, error);
        }

        probes?.Report(roleId, original.Name, raw.Status != HealthStatus.Healthy);
        if (!cap || raw.Status != HealthStatus.Unhealthy) return raw;
        var data = new Dictionary<string, object>(raw.Data) { ["roleId"] = roleId, ["rawStatus"] = raw.Status.ToString() };
        return HealthCheckResult.Degraded(raw.Description, raw.Exception, data);
    }
}
