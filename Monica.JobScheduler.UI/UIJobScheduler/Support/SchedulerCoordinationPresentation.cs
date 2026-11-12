using Monica.JobScheduler.Models.Operations;

namespace Monica.JobScheduler.UI.UIJobScheduler.Support;

/// <summary>Classifies retained coordination evidence against its store observation, independently of cycle readiness.</summary>
internal static class SchedulerCoordinationPresentation
{
    internal static string? Owner(JobCoordinatorSummary row) => row.CoordinatorKey.StartsWith("owner:", StringComparison.Ordinal)
        ? row.CoordinatorKey[6..] : null;

    internal static string State(JobCoordinatorSummary row, JobSchedulerRuntimeOverview overview) =>
        row.ExpiresAtUtc <= overview.Coordination!.ObservedAtUtc ? "expired"
            : row.WorkerInstanceId == overview.Configuration.WorkerInstanceId ? "active" : "standby";

    internal static string Role(string state) => state == "active" ? "info" : state == "expired" ? "warning" : "neutral";

    internal static (string State, int Count) OwnerDuty(JobSchedulerRuntimeOverview overview)
    {
        if (!overview.Configuration.AutonomousWork) return ("manual", 0);
        if (overview.Configuration.OwnerKeys.Count == 0) return ("none", 0);
        if (overview.Coordination is null) return ("unobserved", 0);
        var rows = overview.Coordination.Coordinators.Where(row => Owner(row) is { } owner
            && overview.Configuration.OwnerKeys.Contains(owner, StringComparer.Ordinal)).ToArray();
        var local = rows.Count(row => State(row, overview) == "active");
        if (local > 0) return ("active", local);
        var expired = rows.Count(row => State(row, overview) == "expired");
        if (expired > 0) return ("expired", expired);
        return rows.Length == overview.Configuration.OwnerKeys.Count ? ("standby", rows.Length) : ("unobserved", 0);
    }

    internal static string MaintenanceDuty(JobSchedulerRuntimeOverview overview)
    {
        if (!overview.Configuration.AutonomousWork) return "manual";
        var row = overview.Coordination?.Coordinators.FirstOrDefault(row => row.CoordinatorKey == "maintenance");
        return row is null ? "unobserved" : State(row, overview);
    }
}
