using Monica.SignalR.Models;
using Monica.UI.Shared.Components.Visual;

namespace Monica.SignalR.UISignalR.Support;

/// <summary>Derives SignalR display judgments without widening the underlying evidence boundary.</summary>
internal static class SignalRDiagnosticRules
{
    internal const double SPIKE_THRESHOLD_MS = 1000;

    internal static bool HasObservation(SignalRSendMethodMetricInfo method) => method.CompletedCount + method.FailedCount > 0;
    internal static bool HasSpike(SignalRSendMethodMetricInfo method) => HasObservation(method) && method.MaxDurationMilliseconds >= SPIKE_THRESHOLD_MS;
    internal static bool NeedsAttention(SignalRSendMethodMetricInfo method) => method.PendingSendCount > 0 || method.FailedCount > 0 || HasSpike(method);
    internal static string Standing(SignalRSendMethodMetricInfo method) => method.FailedCount > 0 ? "Failed"
        : method.PendingSendCount > 0 ? "Inflight" : HasSpike(method) ? "Spike" : "Healthy";
    internal static MoVisualRole SendRole(SignalRSendMethodMetricInfo method) => Standing(method) switch
    {
        "Failed" => MoVisualRole.Error,
        "Inflight" => MoVisualRole.Info,
        "Spike" => MoVisualRole.Warning,
        _ => MoVisualRole.Success
    };
    internal static MoVisualRole StatusRole(string status) => status switch
    {
        "Connected" => MoVisualRole.Success,
        "Connecting" or "Reconnecting" => MoVisualRole.Warning,
        _ => MoVisualRole.Neutral
    };
    internal static MoVisualRole TraceRole(string type) => type switch
    {
        "Sent" => MoVisualRole.Primary,
        "Received" => MoVisualRole.Info,
        "Success" => MoVisualRole.Success,
        "Error" => MoVisualRole.Error,
        _ => MoVisualRole.Neutral
    };
    internal static MoVisualRole TargetRole(SignalRSendTargetKind kind) => kind switch
    {
        SignalRSendTargetKind.All or SignalRSendTargetKind.AllExcept => MoVisualRole.Third,
        SignalRSendTargetKind.Group or SignalRSendTargetKind.Groups or SignalRSendTargetKind.GroupExcept => MoVisualRole.Info,
        SignalRSendTargetKind.Client or SignalRSendTargetKind.Clients => MoVisualRole.Primary,
        SignalRSendTargetKind.User or SignalRSendTargetKind.Users => MoVisualRole.Success,
        _ => MoVisualRole.Neutral
    };

    internal static bool Matches(SignalRConnectedUserInfo connection, string query) => string.IsNullOrWhiteSpace(query)
        || Contains(connection.ConnectionId, query) || Contains(connection.UserName, query) || Contains(connection.UserId, query)
        || connection.Claims.Any(claim => Contains(claim.Key, query) || Contains(claim.Value, query));

    internal static string UserKey(SignalRConnectedUserInfo connection) => !string.IsNullOrEmpty(connection.UserId)
        ? $"id:{connection.UserId}" : !string.IsNullOrEmpty(connection.UserName)
            ? $"name:{connection.UserName}" : $"connection:{connection.ConnectionId}";

    internal static List<List<SignalRConnectedUserInfo>> GroupConnections(IEnumerable<SignalRConnectedUserInfo> connections, string browserId) => connections
        .GroupBy(UserKey, StringComparer.Ordinal)
        .Select(group => group.OrderBy(connection => connection.ConnectionTime).ToList())
        .OrderByDescending(group => group.Any(connection => connection.ConnectionId == browserId))
        .ThenByDescending(group => group.Count)
        .ThenBy(group => group[0].ConnectionTime)
        .ToList();

    internal static bool Matches(SignalRSendMethodMetricInfo method, string query) => string.IsNullOrWhiteSpace(query)
        || Contains(method.HubName, query) || Contains(method.MethodName, query)
        || method.TargetMetrics.Any(target => Contains(target.TargetKind.ToString(), query)
            || target.TargetIdentifiers.Any(value => Contains(value, query))
            || target.TargetIdentifierDisplayNames.Any(value => Contains(value, query)));

    private static bool Contains(string? value, string query) => value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true;

    internal static SignalRConnectedUserInfo Freeze(SignalRConnectedUserInfo connection) => new()
    {
        ConnectionId = connection.ConnectionId,
        ConnectionTime = connection.ConnectionTime,
        IsAuthenticated = connection.IsAuthenticated,
        UserId = connection.UserId,
        UserName = connection.UserName,
        Claims = new(connection.Claims, StringComparer.Ordinal)
    };
}
