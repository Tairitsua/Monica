using Monica.StateStore.Abstractions;
using Monica.StateStore.UI.Models;
using Monica.StateStore.UI.Pages;
using Monica.UI.Icons;

namespace Monica.StateStore.UI.Support;

internal static class StateStoreUi
{
    public static string Browser(string? identity) => WithProvider(UIStateStoreDashboardPage.PAGE_URL, identity);
    public static string Providers(string? identity) => WithProvider(UIStateStoreProvidersPage.PAGE_URL, identity);
    private static string WithProvider(string route, string? identity) =>
        identity is null ? route : $"{route}?provider={Uri.EscapeDataString(identity)}";

    public static string Tone(StateStoreProviderInfo provider) => provider.ProviderType switch
    {
        EStateStoreProviderType.Memory => "info",
        EStateStoreProviderType.Redis => "primary",
        _ => "secondary"
    };

    public static string Icon(StateStoreProviderInfo provider) => provider.ProviderType switch
    {
        EStateStoreProviderType.Memory => MoIcons.Cpu,
        EStateStoreProviderType.Dapr => MoIcons.Layers,
        _ => MoIcons.Database
    };

    public static (string Prefix, string Name) SplitKey(string key)
    {
        var first = key.IndexOf(':');
        if (first < 0) return (string.Empty, key);
        var second = key.IndexOf(':', first + 1);
        var split = second < 0 ? first : second;
        return (key[..split], key[(split + 1)..]);
    }
}
