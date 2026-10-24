namespace Monica.EventBus.Models;

/// <summary>
/// EventSubscription lifecycle states with clear state machine transitions.
/// </summary>
public enum EventSubscriptionState
{
    /// <summary>
    /// EventSubscription created but not yet active (initial state).
    /// </summary>
    Pending = 0,

    /// <summary>
    /// EventSubscription is active and receiving events.
    /// </summary>
    Active = 1,

    /// <summary>
    /// EventSubscription is temporarily deactivated but can be reactivated.
    /// </summary>
    Inactive = 2,

    /// <summary>
    /// EventSubscription has been disposed and cannot be reused.
    /// </summary>
    Disposed = 3,

    /// <summary>Locally eligible for explicit test delivery, but never attached to a transport.</summary>
    Held = 4,

    /// <summary>The owning role is not eligible in this process.</summary>
    Disabled = 5
}
