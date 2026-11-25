using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Monica.Framework.UI.UIObservableInstance.Models;

namespace Monica.Framework.UI.UIObservableInstance.Support;

/// <summary>
/// Circuit-owned detached capture shared between the two diagnostic views.
/// Leaving the workspace releases the capture. Filters and evidence selection belong to the URL.
/// </summary>
public sealed class ObservableWorkspaceState : IDisposable
{
    private readonly NavigationManager _navigation;

    /// <summary>Creates the scoped owner and borrows navigation to detect departure from the workspace.</summary>
    public ObservableWorkspaceState(NavigationManager navigation)
    {
        _navigation = navigation;
        _navigation.LocationChanged += LocationChanged;
    }

    internal ObservableEvidence? Evidence { get; set; }
    internal string? ReadError { get; set; }

    private void LocationChanged(object? sender, LocationChangedEventArgs args)
    {
        if (ObservableViewQuery.OwnsPath(_navigation, args.Location)) return;
        Evidence = null;
        ReadError = null;
    }

    /// <summary>Unsubscribes from navigation and releases the detached capture.</summary>
    public void Dispose()
    {
        _navigation.LocationChanged -= LocationChanged;
        Evidence = null;
    }
}
