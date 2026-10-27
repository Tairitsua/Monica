using Monica.Locker.Leases;

namespace Monica.ServiceDiscovery.Models;

/// <summary>
/// Leader election configuration
/// </summary>
public class ElectionConfig
{
    /// <summary>
    /// Heartbeat period in seconds. Default: 15 seconds
    /// </summary>
    public int HeartbeatPeriodSeconds { get; set; } = 15;

    /// <summary>
    /// Registration offline TTL multiplier. Default: 3x
    /// </summary>
    public double RegistrationOfflineTTLMultiplier { get; set; } = 3;

    /// <summary>
    /// Additional seconds for registration offline TTL. Default: 1 second
    /// </summary>
    public int RegistrationOfflineTTLAdditionalSeconds { get; set; } = 1;

    /// <summary>
    /// Leader offline TTL multiplier. Default: 2x
    /// </summary>
    public double LeaderOfflineTTLMultiplier { get; set; } = 2;

    /// <summary>
    /// Additional seconds for leader offline TTL. Default: 1 second
    /// </summary>
    public int LeaderOfflineTTLAdditionalSeconds { get; set; } = 1;

    /// <summary>
    /// Heartbeat jitter range in milliseconds. Default: 1000 milliseconds
    /// </summary>
    public int HeartbeatJitterMilliseconds { get; set; } = 1000;

    /// <summary>Election retry and membership-expiry observation interval; defaults to five seconds.</summary>
    public TimeSpan LeaseStandbyInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum lease operation wait; defaults to five seconds and must be less than one third of LeaderTTL.</summary>
    public TimeSpan LeaseOperationTimeout { get; set; } = TimeSpan.FromSeconds(5);

    internal LeaseCoordinatorOptions CreateLeaseOptions() => new()
    {
        Duration = LeaderTTL, StandbyInterval = LeaseStandbyInterval, OperationTimeout = LeaseOperationTimeout
    };

    #region Computed Properties

    /// <summary>
    /// Heartbeat period
    /// </summary>
    public TimeSpan HeartbeatPeriod => TimeSpan.FromSeconds(HeartbeatPeriodSeconds);

    /// <summary>
    /// Registration offline TTL
    /// </summary>
    public TimeSpan RegistrationTTL => TimeSpan.FromSeconds(
        HeartbeatPeriodSeconds * RegistrationOfflineTTLMultiplier + RegistrationOfflineTTLAdditionalSeconds);

    /// <summary>
    /// Leader offline TTL
    /// </summary>
    public TimeSpan LeaderTTL => TimeSpan.FromSeconds(
        HeartbeatPeriodSeconds * LeaderOfflineTTLMultiplier + LeaderOfflineTTLAdditionalSeconds);

    #endregion

    /// <summary>
    /// Validate configuration
    /// </summary>
    public void Validate()
    {
        CreateLeaseOptions().Validate();
        if (HeartbeatJitterMilliseconds < 0 || HeartbeatJitterMilliseconds >= HeartbeatPeriod.TotalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(HeartbeatJitterMilliseconds));
        if (HeartbeatPeriodSeconds <= 0)
            throw new ArgumentException("Heartbeat period must be greater than 0", nameof(HeartbeatPeriodSeconds));

        if (RegistrationOfflineTTLMultiplier <= 0)
            throw new ArgumentException("Registration offline TTL multiplier must be greater than 0", nameof(RegistrationOfflineTTLMultiplier));

        if (LeaderOfflineTTLMultiplier <= 0)
            throw new ArgumentException("Leader offline TTL multiplier must be greater than 0", nameof(LeaderOfflineTTLMultiplier));
    }
}
