using Monica.EventBus.Models;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.Framework.UI.UIEventBus.Models;

/// <summary>
/// Subscribe to change view model
/// </summary>
public class SubscriptionChangeViewModel
{
    /// <summary>
    /// Change type
    /// </summary>
    public EventSubscriptionChangeType ChangeType { get; set; }

    /// <summary>
    /// The color corresponding to the change type
    /// </summary>
    public Color ChangeTypeColor => ChangeType switch
    {
        EventSubscriptionChangeType.Added => Color.Success,
        EventSubscriptionChangeType.Activated => Color.Info,
        EventSubscriptionChangeType.Deactivated => Color.Warning,
        EventSubscriptionChangeType.Removed => Color.Error,
        _ => Color.Default
    };

    /// <summary>
    /// Icon corresponding to the change type
    /// </summary>
    public string ChangeTypeIcon => ChangeType switch
    {
        EventSubscriptionChangeType.Added => MoIcons.AddCircle,
        EventSubscriptionChangeType.Activated => MoIcons.PlayCircle,
        EventSubscriptionChangeType.Deactivated => MoIcons.PauseCircle,
        EventSubscriptionChangeType.Removed => MoIcons.MinusCircle,
        _ => MoIcons.Circle
    };

    /// <summary>
    /// Related subscriptions
    /// </summary>
    public SubscriptionViewModel Subscription { get; set; } = new();

    /// <summary>
    /// Change timestamp
    /// </summary>
    public DateTime Timestamp { get; set; }

}
