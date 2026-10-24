using Monica.EventBus.Models;
using Monica.Core.TypeDiscovery.Abstractions;
using MudBlazor;

namespace Monica.Framework.UI.UIEventBus.Models;

/// <summary>
/// UI friendly subscription view model
/// </summary>
public class SubscriptionViewModel
{
    #region Identity

    /// <summary>
    /// Subscription ID
    /// </summary>
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>
    /// Event type full name
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Event type short name (for display)
    /// </summary>
    public string EventTypeShortName { get; set; } = string.Empty;

    /// <summary>
    /// Topic name
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Service key (for Keyed EventBus)
    /// </summary>
    public string? ServiceKey { get; set; }

    #endregion

    #region Handler Information

    /// <summary>
    /// Processor type full name
    /// </summary>
    public string? HandlerType { get; set; }

    /// <summary>
    /// Processor type short name (for display)
    /// </summary>
    public string? HandlerTypeShortName { get; set; }

    /// <summary>
    /// Processor factory type name
    /// </summary>
    public string HandlerFactoryType { get; set; } = string.Empty;

    /// <summary>
    /// Whether it is an Action processor
    /// </summary>
    public bool IsActionHandler => HandlerType == null;

    #endregion

    #region Action Handler Metadata

    /// <summary>
    /// Action processor method name
    /// </summary>
    public string? ActionMethodName { get; set; }

    /// <summary>
    /// Declared type of action handler
    /// </summary>
    public string? ActionDeclaringType { get; set; }

    /// <summary>
    /// Action handler method signature
    /// </summary>
    public string? ActionMethodSignature { get; set; }

    /// <summary>
    /// Whether the method of the Action processor is a static method
    /// </summary>
    public bool? ActionIsStatic { get; set; }

    #endregion

    #region Scope & State

    /// <summary>
    /// Subscription scope
    /// </summary>
    public EventSubscriptionScope Scope { get; set; }

    /// <summary>
    /// Subscription status
    /// </summary>
    public EventSubscriptionState State { get; set; }

    /// <summary>Gets or sets the immutable automatic-work policy projected by the registry.</summary>
    public ActivationMode Activation { get; set; } = ActivationMode.Automatic;

    /// <summary>Gets whether automatic receipt can be activated without changing the startup policy.</summary>
    public bool CanActivate => Activation == ActivationMode.Automatic &&
        State is EventSubscriptionState.Pending or EventSubscriptionState.Inactive;

    /// <summary>Gets whether explicit delivery to this one local handler is allowed.</summary>
    public bool CanDeliverLocally => Activation != ActivationMode.Disabled &&
        State is EventSubscriptionState.Active or EventSubscriptionState.Held;

    /// <summary>
    /// The color corresponding to the subscription status
    /// </summary>
    public Color StateColor => State switch
    {
        EventSubscriptionState.Active => Color.Success,
        EventSubscriptionState.Pending => Color.Warning,
        EventSubscriptionState.Inactive => Color.Default,
        EventSubscriptionState.Disposed => Color.Error,
        EventSubscriptionState.Held => Color.Info,
        EventSubscriptionState.Disabled => Color.Default,
        _ => Color.Default
    };

    /// <summary>
    /// Runtime health of the underlying topic subscription, reported by the distributed provider.
    /// <see langword="null"/> for local subscriptions or before the provider reports anything.
    /// </summary>
    public TopicSubscriptionStatus? TopicStatus { get; set; }

    /// <summary>
    /// The runtime state of the underlying topic subscription, if reported.
    /// </summary>
    public TopicSubscriptionRuntimeState? TopicRuntimeState => TopicStatus?.State;

    /// <summary>
    /// Whether the status chip must display the runtime health instead of the registration state:
    /// true for distributed subscriptions registered as Active whose topic is not currently healthy.
    /// </summary>
    public bool IsRuntimeStateDisplayed =>
        Scope == EventSubscriptionScope.Distributed &&
        State == EventSubscriptionState.Active &&
        TopicStatus is { State: not TopicSubscriptionRuntimeState.Healthy };

    /// <summary>
    /// Whether the subscription is registered as Active but its topic is currently not delivering
    /// messages (recovering after failures or terminally failed).
    /// </summary>
    public bool IsUnhealthy =>
        Scope == EventSubscriptionScope.Distributed &&
        State == EventSubscriptionState.Active &&
        TopicStatus is { State: TopicSubscriptionRuntimeState.Recovering or TopicSubscriptionRuntimeState.Failed };

    /// <summary>
    /// The color corresponding to a topic subscription runtime state.
    /// </summary>
    public static Color GetRuntimeStateColor(TopicSubscriptionRuntimeState state) => state switch
    {
        TopicSubscriptionRuntimeState.Healthy => Color.Success,
        TopicSubscriptionRuntimeState.Subscribing => Color.Info,
        TopicSubscriptionRuntimeState.Recovering => Color.Warning,
        TopicSubscriptionRuntimeState.Failed or TopicSubscriptionRuntimeState.Stopped => Color.Error,
        _ => Color.Default
    };

    /// <summary>
    /// The effective status chip color: runtime health color when the runtime state is displayed,
    /// otherwise the registration state color.
    /// </summary>
    public Color DisplayStateColor =>
        IsRuntimeStateDisplayed && TopicStatus is { } status ? GetRuntimeStateColor(status.State) : StateColor;

    /// <summary>
    /// The color corresponding to the subscription range
    /// </summary>
    public Color ScopeColor => Scope == EventSubscriptionScope.Local ? Color.Info : Color.Secondary;

    #endregion

    #region Lifecycle

    /// <summary>
    /// creation time
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// activation time
    /// </summary>
    public DateTime? ActivatedAt { get; set; }

    /// <summary>
    /// deactivation time
    /// </summary>
    public DateTime? DeactivatedAt { get; set; }

    /// <summary>
    /// Whether to automatically discover
    /// </summary>
    public bool IsAutoDiscovered { get; set; }

    #endregion

    #region Metadata

    /// <summary>
    /// metadata
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    #endregion

    #region Display Properties

    /// <summary>
    /// Creation time display text
    /// </summary>
    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// Activation time display text
    /// </summary>
    public string? ActivatedAtDisplay => ActivatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// Disable time display text
    /// </summary>
    public string? DeactivatedAtDisplay => DeactivatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// Calculates the duration represented by the current subscription lifecycle state.
    /// </summary>
    /// <returns>
    /// The active or completed duration, or <see langword="null"/> when the lifecycle has not produced a duration yet.
    /// </returns>
    public TimeSpan? GetDuration()
    {
        if (State == EventSubscriptionState.Active && ActivatedAt.HasValue)
        {
            return DateTime.UtcNow - ActivatedAt.Value;
        }

        if (State == EventSubscriptionState.Inactive && DeactivatedAt.HasValue && ActivatedAt.HasValue)
        {
            return DeactivatedAt.Value - ActivatedAt.Value;
        }

        if (State == EventSubscriptionState.Disposed && CreatedAt != default)
        {
            var endTime = DeactivatedAt ?? ActivatedAt ?? DateTime.UtcNow;
            return endTime - CreatedAt;
        }

        return null;
    }

    #endregion
}
