using Microsoft.Extensions.Localization;
using Monica.EventBus.Models;
using Monica.Framework.UI.Localization;
using Monica.UI.Icons;

namespace Monica.Framework.UI.UIEventBus.Support;

internal static class EventBusVisuals
{
    internal static string Role(EventBusCoverage coverage) => coverage switch
    {
        EventBusCoverage.Failed => "error",
        EventBusCoverage.Recovering or EventBusCoverage.HealthyErr => "warning",
        EventBusCoverage.Healthy => "success",
        EventBusCoverage.Subscribing or EventBusCoverage.Local => "info",
        _ => "neutral"
    };

    internal static string Role(EventSubscriptionState state) => state switch
    {
        EventSubscriptionState.Active => "success",
        EventSubscriptionState.Held => "info",
        EventSubscriptionState.Pending => "warning",
        EventSubscriptionState.Disposed => "error",
        _ => "neutral"
    };

    internal static string Role(EventSubscriptionChangeType change) => change switch
    {
        EventSubscriptionChangeType.Added => "success",
        EventSubscriptionChangeType.Activated => "info",
        EventSubscriptionChangeType.Deactivated => "warning",
        EventSubscriptionChangeType.Removed => "error",
        _ => "neutral"
    };

    internal static string CoverageText(this IStringLocalizer<EventBusResource> text, EventBusCoverage coverage) => coverage switch
    {
        EventBusCoverage.Failed => text.GetTopicRuntimeStateText(TopicSubscriptionRuntimeState.Failed),
        EventBusCoverage.Recovering => text.GetTopicRuntimeStateText(TopicSubscriptionRuntimeState.Recovering),
        EventBusCoverage.Subscribing => text.GetTopicRuntimeStateText(TopicSubscriptionRuntimeState.Subscribing),
        EventBusCoverage.HealthyErr => text["Precision:HealthyErr"],
        EventBusCoverage.Healthy => text.GetTopicRuntimeStateText(TopicSubscriptionRuntimeState.Healthy),
        EventBusCoverage.Stopped => text.GetTopicRuntimeStateText(TopicSubscriptionRuntimeState.Stopped),
        EventBusCoverage.Unreported => text["Precision:Unreported"],
        _ => text["Precision:LocalRuntime"]
    };

    internal static string ProviderIcon(bool distributed) => distributed ? MoIcons.Cloud : MoIcons.Cpu;

    internal static string TimeTag(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O");

    internal static string NameSpace(string? fullName, string? shortName)
    {
        if (fullName is null || shortName is null || fullName.Length <= shortName.Length) return "";
        return fullName.EndsWith("." + shortName, StringComparison.Ordinal)
            ? fullName[..^(shortName.Length + 1)] : fullName;
    }
}
