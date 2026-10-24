namespace Monica.Framework.UI.UIEventBus.Models;

/// <summary>
/// Subscription statistics
/// </summary>
public class SubscriptionStatistics
{
    #region Overall Statistics

    /// <summary>
    /// Total number of subscriptions
    /// </summary>
    public int TotalSubscriptions { get; set; }

    /// <summary>
    /// Number of active subscriptions
    /// </summary>
    public int ActiveSubscriptions { get; set; }

    /// <summary>
    /// Number of inactive subscriptions
    /// </summary>
    public int InactiveSubscriptions { get; set; }

    /// <summary>
    /// Number of subscriptions to be activated
    /// </summary>
    public int PendingSubscriptions { get; set; }

    /// <summary>
    /// Number of subscriptions released
    /// </summary>
    public int DisposedSubscriptions { get; set; }

    /// <summary>Number of handlers held for explicit local delivery.</summary>
    public int HeldSubscriptions { get; set; }

    /// <summary>Number of handlers excluded by the startup policy.</summary>
    public int DisabledSubscriptions { get; set; }

    /// <summary>
    /// Number of subscriptions registered as active whose underlying topic subscription is
    /// recovering or terminally failed, and therefore not actually receiving messages.
    /// </summary>
    public int UnhealthySubscriptions { get; set; }

    /// <summary>
    /// Number of distributed topic subscriptions currently recovering or terminally failed.
    /// </summary>
    public int UnhealthyTopics { get; set; }

    #endregion

    #region Scope Statistics

    /// <summary>
    /// Number of local subscriptions
    /// </summary>
    public int LocalSubscriptions { get; set; }

    /// <summary>
    /// Number of distributed subscriptions
    /// </summary>
    public int DistributedSubscriptions { get; set; }

    #endregion

    #region Discovery Statistics

    /// <summary>
    /// Automatically discover the number of subscriptions
    /// </summary>
    public int AutoDiscoveredCount { get; set; }

    /// <summary>
    /// Number of manual subscriptions
    /// </summary>
    public int ManualSubscriptionCount { get; set; }

    #endregion

    #region Handler Statistics

    /// <summary>
    /// Number of Action Processor Subscriptions
    /// </summary>
    public int ActionHandlerCount { get; set; }

    /// <summary>
    /// Number of type handler subscriptions
    /// </summary>
    public int TypeHandlerCount { get; set; }

    #endregion

    #region Top Lists

    /// <summary>
    /// Top event type list (sorted by number of subscriptions)
    /// </summary>
    public List<EventTypeCount> TopEventTypes { get; set; } = new();

    /// <summary>
    /// Top topic list (sorted by number of subscriptions)
    /// </summary>
    public List<TopicCount> TopTopics { get; set; } = new();

    #endregion

    #region Computed Properties

    /// <summary>
    /// Activity rate (percentage)
    /// </summary>
    public double ActiveRate => TotalSubscriptions > 0
        ? (double)ActiveSubscriptions / TotalSubscriptions * 100
        : 0;

    /// <summary>
    /// Activity rate display text
    /// </summary>
    public string ActiveRateDisplay => $"{ActiveRate:F1}%";

    /// <summary>
    /// Automatic discovery rate (percentage)
    /// </summary>
    public double AutoDiscoveredRate => TotalSubscriptions > 0
        ? (double)AutoDiscoveredCount / TotalSubscriptions * 100
        : 0;

    /// <summary>
    /// Automatic discovery rate display text
    /// </summary>
    public string AutoDiscoveredRateDisplay => $"{AutoDiscoveredRate:F1}%";

    #endregion
}

/// <summary>
/// Event type statistics
/// </summary>
public class EventTypeCount
{
    /// <summary>
    /// Event type name
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Event type short name
    /// </summary>
    public string EventTypeShortName { get; set; } = string.Empty;

    /// <summary>
    /// Number of subscriptions
    /// </summary>
    public int Count { get; set; }
}

/// <summary>
/// Topic Statistics
/// </summary>
public class TopicCount
{
    /// <summary>
    /// Topic name
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Number of subscriptions
    /// </summary>
    public int Count { get; set; }
}
