using System.Text.Json.Serialization;

namespace Monica.Framework.ChainTracing.Models;

/// <summary>Observed EF lifecycle boundary. These phases do not claim database-server-only execution time.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatabaseTracePhase>))]
public enum DatabaseTracePhase
{
    /// <summary>Opening a logical connection, including any unseparated pool, transport, or authentication wait.</summary>
    [JsonStringEnumMemberName("connection.open")]
    ConnectionOpen,
    /// <summary>Executing a command until its result or reader is returned.</summary>
    [JsonStringEnumMemberName("command.execute")]
    CommandExecute,
    /// <summary>Reader returned until closing begins, including materialization and consumer pauses.</summary>
    [JsonStringEnumMemberName("reader.consume")]
    ReaderConsume
}

/// <summary>Database-phase evidence. Durations are milliseconds; unavailable subphase measurements remain null.</summary>
public sealed class DatabaseTraceDetails
{
    /// <summary>The measured lifecycle boundary.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DatabaseTracePhase Phase { get; init; }
    /// <summary>EF context type, when supplied by EF.</summary>
    public string? ContextType { get; init; }
    /// <summary>EF logical connection identifier, not a physical socket or pool-lease identifier.</summary>
    public Guid ConnectionId { get; init; }
    /// <summary>Command identifier for one execution; null when a node aggregates multiple executions.</summary>
    [JsonInclude]
    public Guid? CommandId { get; internal set; }
    /// <summary>Number of completed executions represented by this node.</summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int ExecutionCount { get; internal set; }
    /// <summary>Number of started operations without a terminal observation. Nonzero means timing is incomplete.</summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int PendingCount { get; internal set; }
    /// <summary>Sum of observed completed-execution durations; excludes the gaps between executions.</summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double TotalDurationMs { get; internal set; }
    /// <summary>Largest completed-execution duration.</summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double MaxDurationMs { get; internal set; }
    /// <summary>Wall-time window from the first start through the latest completion; may include gaps or overlap.</summary>
    [JsonInclude]
    public double? WindowDurationMs { get; internal set; }
    /// <summary>Request-correlated pool wait, if a provider supplies it. EF lifecycle interception alone cannot separate it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? PoolWaitMs { get; init; }
    /// <summary>Request-correlated physical connection establishment, if observed by the provider; otherwise null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? PhysicalConnectMs { get; init; }
}
