using Microsoft.Extensions.Localization;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Framework.UI.UIEventBus.Models;

namespace Monica.Framework.UI.Localization;

/// <summary>
/// Provides display text helpers for EventBus UI localization.
/// </summary>
public static class EventBusLocalizationExtensions
{
    /// <summary>
    /// Gets localized display text for a subscription scope.
    /// </summary>
    public static string GetSubscriptionScopeText(this IStringLocalizer localizer, EventSubscriptionScope scope)
    {
        return scope switch
        {
            EventSubscriptionScope.Local => localizer["Shared:Scopes:Local"].Value,
            EventSubscriptionScope.Distributed => localizer["Shared:Scopes:Distributed"].Value,
            _ => localizer["Shared:Labels:Unknown"].Value
        };
    }

    /// <summary>
    /// Gets localized display text for a subscription state.
    /// </summary>
    public static string GetSubscriptionStateText(this IStringLocalizer localizer, EventSubscriptionState state)
    {
        return state switch
        {
            EventSubscriptionState.Pending => localizer["Shared:SubscriptionStates:Pending"].Value,
            EventSubscriptionState.Active => localizer["Shared:SubscriptionStates:Active"].Value,
            EventSubscriptionState.Inactive => localizer["Shared:SubscriptionStates:Inactive"].Value,
            EventSubscriptionState.Disposed => localizer["Shared:SubscriptionStates:Disposed"].Value,
            EventSubscriptionState.Held => localizer["Shared:SubscriptionStates:Held"].Value,
            EventSubscriptionState.Disabled => localizer["Shared:SubscriptionStates:Disabled"].Value,
            _ => localizer["Shared:Labels:Unknown"].Value
        };
    }

    /// <summary>
    /// Gets localized display text for a topic subscription runtime state.
    /// </summary>
    public static string GetTopicRuntimeStateText(this IStringLocalizer localizer, TopicSubscriptionRuntimeState state)
    {
        return state switch
        {
            TopicSubscriptionRuntimeState.Subscribing => localizer["Shared:RuntimeStates:Subscribing"].Value,
            TopicSubscriptionRuntimeState.Healthy => localizer["Shared:RuntimeStates:Healthy"].Value,
            TopicSubscriptionRuntimeState.Recovering => localizer["Shared:RuntimeStates:Recovering"].Value,
            TopicSubscriptionRuntimeState.Failed => localizer["Shared:RuntimeStates:Failed"].Value,
            TopicSubscriptionRuntimeState.Stopped => localizer["Shared:RuntimeStates:Stopped"].Value,
            _ => localizer["Shared:Labels:Unknown"].Value
        };
    }

    /// <summary>
    /// Gets the effective localized status text for a subscription row: the runtime health text
    /// when the runtime state overrides the display, otherwise the registration state text.
    /// </summary>
    public static string GetSubscriptionDisplayStateText(this IStringLocalizer localizer, SubscriptionViewModel subscription)
    {
        return subscription.IsRuntimeStateDisplayed && subscription.TopicStatus is { } status
            ? localizer.GetTopicRuntimeStateText(status.State)
            : localizer.GetSubscriptionStateText(subscription.State);
    }

    /// <summary>
    /// Builds the tooltip text describing the most recent error of a subscription's topic,
    /// or an empty string when no error was reported.
    /// </summary>
    public static string BuildTopicRuntimeErrorTooltip(this IStringLocalizer localizer, SubscriptionViewModel subscription, Monica.UI.Shared.Services.MoTimeFormatter time)
    {
        if (subscription.TopicStatus is not { LastErrorMessage: { } message } status)
        {
            return string.Empty;
        }

        var occurredAt = status.LastErrorAt.HasValue
            ? localizer.FormatEventBusRelativeTime(status.LastErrorAt.Value, time)
            : string.Empty;
        return localizer["SubscriptionTable:Labels:RuntimeError", message, occurredAt].Value;
    }

    /// <summary>
    /// Gets localized display text for a subscription change type.
    /// </summary>
    public static string GetSubscriptionChangeTypeText(this IStringLocalizer localizer, EventSubscriptionChangeType changeType)
    {
        return changeType switch
        {
            EventSubscriptionChangeType.Added => localizer["Shared:ChangeTypes:Added"].Value,
            EventSubscriptionChangeType.Activated => localizer["Shared:ChangeTypes:Activated"].Value,
            EventSubscriptionChangeType.Deactivated => localizer["Shared:ChangeTypes:Deactivated"].Value,
            EventSubscriptionChangeType.Removed => localizer["Shared:ChangeTypes:Removed"].Value,
            _ => localizer["Shared:Labels:Unknown"].Value
        };
    }

    /// <summary>
    /// Gets localized display text for an EventBus provider kind.
    /// </summary>
    public static string GetProviderKindText(this IStringLocalizer localizer, EventBusProviderKind providerKind)
    {
        return providerKind switch
        {
            EventBusProviderKind.Local => localizer["Shared:ProviderKinds:Local"].Value,
            EventBusProviderKind.Dapr => localizer["Shared:ProviderKinds:Dapr"].Value,
            EventBusProviderKind.Kafka => localizer["Shared:ProviderKinds:Kafka"].Value,
            _ => localizer["Shared:ProviderKinds:Unknown"].Value
        };
    }

    /// <summary>
    /// Gets the localized display name for an EventBus provider registration.
    /// </summary>
    public static string GetProviderDisplayName(this IStringLocalizer localizer, EventBusProviderInfo provider)
    {
        return provider.ServiceKey ?? localizer["Services:Common:DefaultProviderName"].Value;
    }

    /// <summary>
    /// Gets localized display text for a provider bus mode.
    /// </summary>
    public static string GetProviderModeText(this IStringLocalizer localizer, bool isDistributed)
    {
        return isDistributed
            ? localizer["Shared:Scopes:Distributed"].Value
            : localizer["Shared:Scopes:Local"].Value;
    }

    /// <summary>
    /// Gets localized display text for a subscription discovery mode.
    /// </summary>
    public static string GetDiscoveryModeText(this IStringLocalizer localizer, bool isAutoDiscovered)
    {
        return isAutoDiscovered
            ? localizer["Shared:DiscoveryModes:AutoDiscovered"].Value
            : localizer["Shared:DiscoveryModes:ManualSubscription"].Value;
    }

    /// <summary>
    /// Gets localized display text for a subscription handler.
    /// </summary>
    public static string GetHandlerDisplayText(this IStringLocalizer localizer, SubscriptionViewModel subscription)
    {
        if (subscription.IsActionHandler)
        {
            return string.IsNullOrEmpty(subscription.ActionMethodName)
                ? localizer["Shared:Labels:ActionHandler"].Value
                : localizer["Shared:Formats:ActionMethod", subscription.ActionMethodName].Value;
        }

        return subscription.HandlerTypeShortName ?? localizer["Shared:Labels:Unknown"].Value;
    }

    /// <summary>
    /// Builds localized tooltip text for an action handler.
    /// </summary>
    public static string BuildActionHandlerTooltip(this IStringLocalizer localizer, SubscriptionViewModel subscription)
    {
        if (!subscription.IsActionHandler)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        if (!string.IsNullOrEmpty(subscription.ActionDeclaringType))
        {
            parts.Add(localizer["Shared:Formats:FieldValue", localizer["Shared:Fields:DeclaringType"].Value, subscription.ActionDeclaringType].Value);
        }

        if (!string.IsNullOrEmpty(subscription.ActionMethodName))
        {
            parts.Add(localizer["Shared:Formats:FieldValue", localizer["Shared:Fields:MethodName"].Value, subscription.ActionMethodName].Value);
        }

        if (!string.IsNullOrEmpty(subscription.ActionMethodSignature))
        {
            parts.Add(localizer["Shared:Formats:FieldValue", localizer["Shared:Fields:MethodSignature"].Value, subscription.ActionMethodSignature].Value);
        }

        if (subscription.ActionIsStatic.HasValue)
        {
            var value = subscription.ActionIsStatic.Value
                ? localizer["Shared:Common:Yes"].Value
                : localizer["Shared:Common:No"].Value;
            parts.Add(localizer["Shared:Formats:FieldValue", localizer["Shared:Fields:StaticMethod"].Value, value].Value);
        }

        return parts.Count > 0 ? string.Join(Environment.NewLine, parts) : localizer["Shared:Labels:ActionHandler"].Value;
    }

    /// <summary>
    /// Formats the duration represented by a subscription lifecycle state.
    /// </summary>
    public static string GetSubscriptionDurationText(
        this IStringLocalizer localizer,
        SubscriptionViewModel subscription)
    {
        var duration = subscription.GetDuration();
        return duration.HasValue ? localizer.FormatEventBusDuration(duration.Value) : "-";
    }

    /// <summary>
    /// Formats a duration with localized units.
    /// </summary>
    public static string FormatEventBusDuration(this IStringLocalizer localizer, TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return localizer["Shared:Duration:DaysHours", (int)duration.TotalDays, duration.Hours].Value;
        }

        if (duration.TotalHours >= 1)
        {
            return localizer["Shared:Duration:HoursMinutes", (int)duration.TotalHours, duration.Minutes].Value;
        }

        if (duration.TotalMinutes >= 1)
        {
            return localizer["Shared:Duration:MinutesSeconds", (int)duration.TotalMinutes, duration.Seconds].Value;
        }

        return localizer["Shared:Duration:Seconds", Math.Max(0, (int)duration.TotalSeconds)].Value;
    }

    /// <summary>
    /// Formats a relative timestamp with localized units.
    /// </summary>
    public static string FormatEventBusRelativeTime(this IStringLocalizer localizer, DateTime timestamp, Monica.UI.Shared.Services.MoTimeFormatter time)
    {
        var elapsed = DateTime.UtcNow - timestamp;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalSeconds < 60)
        {
            return localizer["Shared:RelativeTime:SecondsAgo", Math.Max(0, (int)elapsed.TotalSeconds)].Value;
        }

        if (elapsed.TotalMinutes < 60)
        {
            return localizer["Shared:RelativeTime:MinutesAgo", (int)elapsed.TotalMinutes].Value;
        }

        if (elapsed.TotalHours < 24)
        {
            return localizer["Shared:RelativeTime:HoursAgo", (int)elapsed.TotalHours].Value;
        }

        if (elapsed.TotalDays < 30)
        {
            return localizer["Shared:RelativeTime:DaysAgo", (int)elapsed.TotalDays].Value;
        }

        return time.Format(timestamp);
    }
}
