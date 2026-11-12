using System.Globalization;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.UI.UIJobScheduler.Support;

/// <summary>Preserves opaque identities and exact half-open intervals when moving from evidence to the ledger.</summary>
internal static class SchedulerRouteBuilder
{
    internal static string Ledger(JobExecutionQuery query, DateTimeOffset? from = null, DateTimeOffset? to = null, string? instanceId = null)
    {
        var values = new List<string>();
        void Add(string key, string? value) { if (value is not null) values.Add($"{key}={Uri.EscapeDataString(value)}"); }
        Add("time", query.CompletedAfterUtc is not null || query.CompletedBeforeUtc is not null ? "completed" : null);
        Add("owner", query.OwnerKey);
        Add("job", query.JobKey);
        Add("q", query.SearchText);
        Add("states", query.States is { Count: > 0 } states ? string.Join(',', states) : null);
        Add("from", from?.ToString("O", CultureInfo.InvariantCulture));
        Add("to", to?.ToString("O", CultureInfo.InvariantCulture));
        Add("instanceId", instanceId);
        return "/job-scheduler/executions" + (values.Count == 0 ? "" : "?" + string.Join('&', values));
    }
}
