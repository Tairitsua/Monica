using Microsoft.Extensions.Localization;
using Monica.EventBus.Models;
using Monica.Framework.UI.Localization;
using Monica.UI.Icons;
using Monica.UI.Shared.Components.Visual;

namespace Monica.Framework.UI.UIEventBus.Support;

internal static class EventBusVisuals
{
    internal static MoVisualRole Role(EventBusCoverage coverage) => coverage switch
    {
        EventBusCoverage.Failed => MoVisualRole.Error,
        EventBusCoverage.Recovering or EventBusCoverage.HealthyErr => MoVisualRole.Warning,
        EventBusCoverage.Healthy => MoVisualRole.Success,
        EventBusCoverage.Subscribing or EventBusCoverage.Local => MoVisualRole.Info,
        _ => MoVisualRole.Neutral
    };

    internal static MoVisualRole Role(EventSubscriptionState state) => state switch
    {
        EventSubscriptionState.Active => MoVisualRole.Success,
        EventSubscriptionState.Held => MoVisualRole.Info,
        EventSubscriptionState.Pending => MoVisualRole.Warning,
        EventSubscriptionState.Disposed => MoVisualRole.Error,
        _ => MoVisualRole.Neutral
    };

    internal static MoVisualRole Role(EventSubscriptionChangeType change) => change switch
    {
        EventSubscriptionChangeType.Added => MoVisualRole.Success,
        EventSubscriptionChangeType.Activated => MoVisualRole.Info,
        EventSubscriptionChangeType.Deactivated => MoVisualRole.Warning,
        EventSubscriptionChangeType.Removed => MoVisualRole.Error,
        _ => MoVisualRole.Neutral
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
