using Microsoft.Extensions.Localization;
using Monica.EventBus.Models;
using Monica.Framework.UI.Localization;
using MudBlazor;

namespace Monica.Framework.UI.UIEventBus.Support;

internal static class EventBusPresentation
{
    internal static Color RuntimeColor(TopicSubscriptionRuntimeState state) => state switch
    {
        TopicSubscriptionRuntimeState.Healthy => Color.Success,
        TopicSubscriptionRuntimeState.Subscribing => Color.Info,
        TopicSubscriptionRuntimeState.Recovering => Color.Warning,
        TopicSubscriptionRuntimeState.Failed => Color.Error,
        _ => Color.Default
    };

    internal static string KeyText(string? key, IStringLocalizer<EventBusResource> localizer) => key switch
    {
        null => localizer["Services:Common:DefaultProviderKeyWithNoKey"].Value,
        "" => localizer["Workbench:EmptyKey"].Value,
        _ when string.IsNullOrWhiteSpace(key) => localizer["Workbench:WhitespaceKey", key.Length].Value,
        _ => key
    };

}
