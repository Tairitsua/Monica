using Microsoft.AspNetCore.Components;
using Monica.SignalR.UISignalR.State;
using Xunit;

namespace Test.Monica.SignalR.UISignalR.Support;

public class SignalRWorkspaceContextTests
{
    [Theory]
    [InlineData("https://host.example/", "signalr-debug/sends")]
    [InlineData("https://host.example/operations/", "signalr-debug/sends")]
    public void RouteState_WhenHostUsesBasePath_ShouldResolveFamilyNavigationAndRetainFilters(string baseUri, string path)
    {
        var navigation = new ReviewNavigation(baseUri, baseUri + path + "?focus=failed&method=Hub.Update#evidence");
        var context = new SignalRWorkspaceContext(null!, navigation, operation => operation());

        Assert.Equal("/signalr-debug/sends", context.Path);
        Assert.Equal(baseUri + "signalr-debug/connections?q=user-7", context.Href("/signalr-debug/connections?q=user-7"));
        context.Set("method", null);
        Assert.Equal("failed", context.Get("focus"));
        Assert.Equal(string.Empty, context.Get("method"));
        Assert.StartsWith(baseUri + path, navigation.Uri, StringComparison.Ordinal);
        Assert.True(navigation.ReplacedHistory);
        context.Set("method", "Hub.Next", push: true);
        Assert.False(navigation.ReplacedHistory);
        Assert.Equal("Hub.Next", context.Get("method"));
    }

    private sealed class ReviewNavigation : NavigationManager
    {
        public ReviewNavigation(string baseUri, string uri) => Initialize(baseUri, uri);
        public bool ReplacedHistory { get; private set; }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Uri = ToAbsoluteUri(uri).ToString();
            ReplacedHistory = options.ReplaceHistoryEntry;
        }
    }
}
