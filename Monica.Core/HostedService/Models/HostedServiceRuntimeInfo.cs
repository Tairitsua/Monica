
using Monica.Core.ObservableInstance.Models;
using Monica.Core.HostedService.Services.Support;

namespace Monica.Core.HostedService.Models;

/// <summary>
/// Provides runtime information about a hosted service including state, health, and history.
/// </summary>
public class HostedServiceRuntimeInfo
{
    private readonly ObservableInstanceTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="HostedServiceRuntimeInfo"/> class.
    /// </summary>
    /// <param name="tracker">The observable tracker that tracks state and history</param>
    public HostedServiceRuntimeInfo(ObservableInstanceTracker tracker)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    }

    /// <summary>
    /// Gets the underlying observable tracker for internal runtime operations.
    /// </summary>
    internal ObservableInstanceTracker Tracker => _tracker;

    internal HostedServiceRoleBinding? Role { get; set; }

    // Service Identity (delegates to tracker)

    /// <summary>
    /// Gets the unique identity of this hosted-service instance within the current host.
    /// </summary>
    public string InstanceId => _tracker.InstanceId;

    /// <summary>
    /// Gets the name of the service
    /// </summary>
    public string ServiceName => _tracker.InstanceName;

    /// <summary>
    /// Gets the type of the service
    /// </summary>
    public Type ServiceType => _tracker.InstanceType ?? typeof(object);

    /// <summary>
    /// Gets the service key for keyed service instances (optional)
    /// </summary>
    public string? ServiceKey => _tracker.InstanceKey;

    // Current State (delegates to tracker with typed state)

    /// <summary>
    /// Gets the current state of the service
    /// </summary>
    public HostedServiceState CurrentState =>
        _tracker.CurrentState is HostedServiceState state ? state : HostedServiceState.NotStarted;

    /// <summary>
    /// Gets the timestamp when the current state was entered
    /// </summary>
    public DateTime StateChangedAt => _tracker.StateChangedAt;

    /// <summary>
    /// Gets the timestamp when the service was registered
    /// </summary>
    public DateTime RegisteredAt => _tracker.RegisteredAt;

    /// <summary>
    /// Gets or sets the timestamp when the service was started (StartAsync called)
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the service was stopped (StopAsync completed)
    /// </summary>
    public DateTime? StoppedAt { get; set; }

    // Health Information

    /// <summary>
    /// Gets a value indicating whether the service is healthy (Running or Executing state)
    /// </summary>
    public bool IsHealthy => CurrentState is HostedServiceState.Running or HostedServiceState.Executing;

    /// <summary>
    /// Gets a value indicating whether the service is in a degraded state
    /// </summary>
    public bool IsDegraded => CurrentState == HostedServiceState.Degraded;

    /// <summary>
    /// Gets a value indicating whether the service is in a faulted state
    /// </summary>
    public bool IsFaulted => CurrentState == HostedServiceState.Faulted;


    // Heartbeat Information (BackgroundService only)

    /// <summary>
    /// Gets or sets the timestamp of the last heartbeat
    /// </summary>
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>
    /// Gets the configured heartbeat interval (null if heartbeat is disabled)
    /// </summary>
    public TimeSpan? HeartbeatInterval { get; init; }

    /// <summary>
    /// Gets a value indicating whether heartbeat monitoring is enabled
    /// </summary>
    public bool IsHeartbeatEnabled => HeartbeatInterval.HasValue;

    /// <summary>
    /// Gets a value indicating whether the heartbeat is healthy
    /// (either heartbeat is disabled or last heartbeat is within acceptable threshold)
    /// </summary>
    public bool IsHeartbeatHealthy =>
        !IsHeartbeatEnabled ||
        (LastHeartbeat.HasValue &&
         (DateTime.UtcNow - LastHeartbeat.Value) < (HeartbeatInterval!.Value * 2));

    // State History (delegates to agent with typed conversion)

    /// <summary>
    /// Gets the state history for this service
    /// </summary>
    public IReadOnlyList<HostedServiceStateTransition> StateHistory =>
        _tracker.GetHistory()
            .Select(h => new HostedServiceStateTransition
            {
                Timestamp = h.Timestamp,
                PreviousState = h.PreviousState as HostedServiceState?,
                CurrentState = h.CurrentState is HostedServiceState s ? s : HostedServiceState.NotStarted,
                Message = h.Message,
                Exception = h.Exception
            })
            .ToList()
            .AsReadOnly();

    /// <summary>
    /// Gets the maximum number of history entries to retain
    /// </summary>
    public int MaxHistorySize => _tracker.MaxHistorySize;

    // Execution Statistics

    /// <summary>
    /// Gets the total number of state changes that have occurred
    /// </summary>
    public long TotalStateChanges => _tracker.TotalStateChanges;

    /// <summary>
    /// Gets the uptime of the service (time since started, null if not started or already stopped)
    /// </summary>
    public TimeSpan? Uptime => StartedAt.HasValue && StoppedAt == null
        ? DateTime.UtcNow - StartedAt.Value
        : null;
}
