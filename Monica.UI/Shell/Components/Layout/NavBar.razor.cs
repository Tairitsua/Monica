using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Monica.Core.Localization.Abstractions;
using Monica.Modules;
using Monica.UI.Icons;
using Monica.UI.Localization;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
using Monica.UI.Shell.State;
using MudBlazor;
using MudBlazor.Interfaces;

namespace Monica.UI.Shell.Components.Layout;

public partial class NavBar : IAsyncDisposable, INavigationEventReceiver
{
    private const int LAYOUT_SAFETY_MARGIN_PX = 8;

    [Inject] private IPageCatalog PageCatalog { get; set; } = default!;
    [Inject] private IOptions<ModuleShellUIOption> Options { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;
    [Inject] private ILocalizationCatalog LocalizationCatalog { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private PageAccessEvaluator PageAccess { get; set; } = default!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] private IServiceProvider ServiceProvider { get; set; } = default!;
    [Inject] private ShellState Shell { get; set; } = default!;

    private readonly List<NavigationGroup> _navigationGroups = [];
    private readonly List<Task> _pendingCallbacks = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    private ElementReference _desktopNavRef;
    private ElementReference _categoryRailRef;
    private ElementReference _measurementRailRef;
    private readonly object _disposeSync = new();
    private NavBarLayoutInteropSession? _layoutInterop;
    private int _visibleCategoryCount;
    private AuthenticationStateProvider? _authenticationStateProvider;
    private Task? _disposeTask;
    private int _navigationRefreshVersion;
    private bool _disposed;
    private NavigationCategoryId? _selectedCategoryId;
    private NavigationCategoryId? _visibleCategoryTooltipId;
    private bool _mobileNavigationOpen;
    private bool _restoreMobileFocus;
    private bool _focusDesktopNavigation;
    private MudIconButton? _mobileNavigationTrigger;
    private readonly Dictionary<NavigationCategoryId, MudIconButton> _contextNavigationTriggers = [];
    private NavigationCategoryId? _contextCategoryId;
    private bool _contextNavigationOpen;
    private bool _restoreContextFocus;
    private string? _contextOpenedRoute;
    private string? _contextFocusRestoreRoute;
    private bool _isMobile;

    private int MaxVisibleCategories => Math.Max(0, Options.Value.MaxVisibleCategories);

    private NavigationItem? ActiveItem => NavigationRouteMatcher.FindActiveItem(
        CurrentRoute, NavigationTreeBuilder.Flatten(_navigationGroups));

    private NavigationGroup? ActiveCategory => _navigationGroups.FirstOrDefault(
        group => ActiveItem is { } active && NavigationTreeBuilder.Flatten([group]).Contains(active));

    private NavigationGroup? SelectedCategory => _navigationGroups.FirstOrDefault(
        group => group.Id == _selectedCategoryId) ?? ActiveCategory ?? _navigationGroups.FirstOrDefault();

    private NavigationGroup? ContextCategory => _navigationGroups.FirstOrDefault(group => group.Id == _contextCategoryId);

    private bool IsContextNavigationOpen => !_isMobile && _contextNavigationOpen && ContextCategory is not null &&
        Shell.Layout == ShellNavigationLayout.Sidebar && Shell.SidebarCollapsed;

    private string CurrentRoute => NavigationManager.ToBaseRelativePath(NavigationManager.Uri);

    private IEnumerable<NavigationGroup> VisibleCategories =>
        _navigationGroups.Take(EffectiveVisibleCategoryCount);

    private bool HasMoreCategories => _navigationGroups.Count > EffectiveVisibleCategoryCount;

    private bool HasAnyCategories => _navigationGroups.Count > 0;

    private IReadOnlyList<NavigationGroup> AllCategories => _navigationGroups;

    private IReadOnlyList<NavigationGroup> MoreCategories =>
        _navigationGroups
            .Skip(EffectiveVisibleCategoryCount)
            .ToList();

    private int EffectiveVisibleCategoryCount =>
        Math.Clamp(_visibleCategoryCount, 0, Math.Min(MaxVisibleCategories, _navigationGroups.Count));

    protected override async Task OnInitializedAsync()
    {
        _layoutInterop = new NavBarLayoutInteropSession(JSRuntime, this);
        NavigationManager.LocationChanged += HandleLocationChanged;
        Shell.OnChanged += HandleShellChanged;
        _authenticationStateProvider = ServiceProvider.GetService<AuthenticationStateProvider>();
        if (_authenticationStateProvider is not null)
        {
            _authenticationStateProvider.AuthenticationStateChanged += HandleAuthenticationStateChanged;
        }

        await RebuildNavigationAsync(_lifetimeCancellation.Token);
    }

    private async Task<bool> RebuildNavigationAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return false;
        }

        var refreshVersion = Interlocked.Increment(ref _navigationRefreshVersion);
        var navigationItems = new List<NavigationItem>();
        foreach (var navigationItem in PageCatalog.GetNavItems())
        {
            if (await PageAccess.IsAuthorizedAsync(navigationItem, cancellationToken))
            {
                navigationItems.Add(navigationItem);
            }
        }

        if (_disposed || refreshVersion != Volatile.Read(ref _navigationRefreshVersion))
        {
            return false;
        }

        _navigationGroups.Clear();
        _navigationGroups.AddRange(NavigationTreeBuilder.Build(
            PageCatalog.GetNavigationCategories(), navigationItems, LocalizationCatalog));

        if (_contextNavigationOpen && ContextCategory is null)
        {
            CloseContextNavigationCore(restoreFocus: false);
        }

        _visibleCategoryCount = Math.Min(MaxVisibleCategories, _navigationGroups.Count);
        return true;
    }

    private void HandleAuthenticationStateChanged(Task<AuthenticationState> authenticationStateTask)
    {
        QueueCallback(async () =>
        {
            try
            {
                await authenticationStateTask;
            }
            catch
            {
                // A failed authentication-state transition is treated as denied by the page policies below.
            }

            if (await RebuildNavigationAsync(_lifetimeCancellation.Token) && !_disposed)
            {
                StateHasChanged();
            }
        });
    }

    private void HandleLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        QueueCallback(() =>
        {
            _visibleCategoryTooltipId = null;
            _selectedCategoryId = ActiveCategory?.Id;
            _mobileNavigationOpen = false;
            _restoreMobileFocus = false;
            _focusDesktopNavigation = false;
            var restoreContextFocus = (_contextNavigationOpen || _restoreContextFocus) &&
                string.Equals(_contextOpenedRoute, CurrentRoute, StringComparison.OrdinalIgnoreCase);
            CloseContextNavigationCore(restoreFocus: restoreContextFocus);
            StateHasChanged();
            return Task.CompletedTask;
        });
    }

    private void HandleShellChanged() => QueueCallback(() =>
    {
        _visibleCategoryTooltipId = null;
        _mobileNavigationOpen = false;
        CloseContextNavigationCore(restoreFocus: false);
        StateHasChanged();
        return Task.CompletedTask;
    });

    private void SetCategoryTooltipVisible(NavigationCategoryId categoryId, bool visible)
    {
        if (_disposed)
        {
            return;
        }

        if (visible)
        {
            _visibleCategoryTooltipId = categoryId;
        }
        else if (_visibleCategoryTooltipId == categoryId)
        {
            _visibleCategoryTooltipId = null;
        }
    }

    private void SelectCategory(NavigationGroup category)
    {
        _visibleCategoryTooltipId = null;
        _selectedCategoryId = category.Id;
    }
    private void OpenMobileNavigation()
    {
        CloseContextNavigationCore(restoreFocus: false);
        _mobileNavigationOpen = true;
    }
    private void CloseMobileNavigation()
    {
        _restoreMobileFocus = _mobileNavigationOpen;
        _mobileNavigationOpen = false;
    }
    Task INavigationEventReceiver.OnNavigation()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        CloseMobileNavigation();
        CloseContextNavigationCore(restoreFocus: string.Equals(_contextOpenedRoute, CurrentRoute, StringComparison.OrdinalIgnoreCase));
        return InvokeAsync(StateHasChanged);
    }

    private bool IsContextCategoryOpen(NavigationGroup category) => IsContextNavigationOpen && _contextCategoryId == category.Id;

    private bool IsSelectedCategory(NavigationGroup category) => IsContextNavigationOpen
        ? _contextCategoryId == category.Id
        : category == SelectedCategory;

    private void OpenContextNavigation(NavigationGroup category)
    {
        _visibleCategoryTooltipId = null;
        if (_disposed || _isMobile || Shell.Layout != ShellNavigationLayout.Sidebar || !Shell.SidebarCollapsed)
        {
            return;
        }

        if (IsContextCategoryOpen(category))
        {
            CloseContextNavigation();
            return;
        }

        _contextCategoryId = category.Id;
        _contextOpenedRoute = CurrentRoute;
        _contextNavigationOpen = true;
        _restoreContextFocus = false;
    }

    private void CloseContextNavigation() => CloseContextNavigationCore(restoreFocus: true);

    private void CloseContextNavigationCore(bool restoreFocus)
    {
        if (restoreFocus && !_contextNavigationOpen)
        {
            return;
        }

        _restoreContextFocus = restoreFocus && _contextNavigationOpen;
        _contextFocusRestoreRoute = _restoreContextFocus ? CurrentRoute : null;
        _contextNavigationOpen = false;
    }

    private void ContextNavigationChanged(bool open)
    {
        if (!open)
        {
            CloseContextNavigation();
        }
    }

    private void HandleContextDrawerKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            _visibleCategoryTooltipId = null;
            CloseContextNavigation();
        }
    }
    private void MobileNavigationChanged(bool open)
    {
        if (open)
        {
            _mobileNavigationOpen = true;
        }
        else
        {
            CloseMobileNavigation();
        }
    }
    private void HandleDrawerKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            CloseMobileNavigation();
        }
    }

    private static string GetCategoryIcon(NavigationGroup group) => group.Icon ?? MoIcons.Folder;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_disposed && _focusDesktopNavigation && !_isMobile)
        {
            _focusDesktopNavigation = false;
            try
            {
                var target = Shell.Layout == ShellNavigationLayout.Sidebar ? _categoryRailRef : _desktopNavRef;
                await target.MudFocusFirstAsync();
            }
            catch (JSDisconnectedException)
            {
                // A disconnected browser no longer has visible desktop navigation to focus.
            }
        }

        if (!_disposed && _restoreContextFocus && !IsContextNavigationOpen)
        {
            _restoreContextFocus = false;
            if (!_isMobile && Shell.Layout == ShellNavigationLayout.Sidebar && Shell.SidebarCollapsed &&
                string.Equals(_contextFocusRestoreRoute, CurrentRoute, StringComparison.OrdinalIgnoreCase) &&
                _contextCategoryId is { } contextCategoryId &&
                _contextNavigationTriggers.TryGetValue(contextCategoryId, out var contextTrigger))
            {
                try
                {
                    await contextTrigger.FocusAsync();
                }
                catch (JSDisconnectedException)
                {
                    // A disconnected browser no longer has a category rail trigger to focus.
                }
            }
        }

        if (!_disposed && _isMobile && _restoreMobileFocus && !_mobileNavigationOpen && _mobileNavigationTrigger is { } trigger)
        {
            _restoreMobileFocus = false;
            try
            {
                await trigger.FocusAsync();
            }
            catch (JSDisconnectedException)
            {
                // A disconnected browser no longer has a navigation trigger to focus.
            }
        }

        if (_disposed || _navigationGroups.Count == 0 || _layoutInterop is not { } layoutInterop)
        {
            return;
        }

        // Observe the desktop rail width so localized labels move into "More"
        // before the navigation spills into the action area.
        await layoutInterop.InitializeAsync(
            _desktopNavRef,
            _measurementRailRef,
            MaxVisibleCategories,
            LAYOUT_SAFETY_MARGIN_PX);
    }

    /// <summary>Updates the measured desktop category capacity and the shell's current viewport mode.</summary>
    /// <param name="visibleCategoryCount">The categories that fit before the overflow menu, including zero for a hidden desktop row.</param>
    /// <param name="isMobile">Whether the viewport matches the shell's maximum-width 929-pixel mobile boundary.</param>
    /// <remarks>The existing layout observer owns notifications. Crossing the boundary dismisses the previous mode's temporary navigation.</remarks>
    [JSInvokable]
    public Task UpdateVisibleCategoryCountAsync(int visibleCategoryCount, bool isMobile)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        return InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            var clampedVisibleCategoryCount = Math.Clamp(
                visibleCategoryCount,
                0,
                Math.Min(MaxVisibleCategories, _navigationGroups.Count));

            if (clampedVisibleCategoryCount == _visibleCategoryCount && _isMobile == isMobile)
            {
                return;
            }

            if (isMobile && !_isMobile)
            {
                _visibleCategoryTooltipId = null;
                _focusDesktopNavigation = false;
                var contextWasOpen = _contextNavigationOpen;
                CloseContextNavigationCore(restoreFocus: false);
                _restoreMobileFocus = contextWasOpen && !_mobileNavigationOpen;
            }
            else if (!isMobile && _isMobile)
            {
                _focusDesktopNavigation = _mobileNavigationOpen;
                _mobileNavigationOpen = false;
                _restoreMobileFocus = false;
            }

            _isMobile = isMobile;
            _visibleCategoryCount = clampedVisibleCategoryCount;
            StateHasChanged();
        });
    }

    /// <summary>Dismisses desktop contextual navigation when native keyboard focus leaves both the panel and category rail.</summary>
    /// <remarks>The layout observer checks visible DOM containment and owns the callback lifetime. Focus remains on the user's new target.</remarks>
    [JSInvokable]
    public Task CloseContextNavigationOnFocusExitAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        return InvokeAsync(() =>
        {
            if (_disposed || !IsContextNavigationOpen)
            {
                return;
            }

            CloseContextNavigationCore(restoreFocus: false);
            StateHasChanged();
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new ValueTask(_disposeTask ??= BeginDispose());
        }
    }

    private Task BeginDispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _navigationRefreshVersion);
        _lifetimeCancellation.Cancel();
        NavigationManager.LocationChanged -= HandleLocationChanged;
        Shell.OnChanged -= HandleShellChanged;
        if (_authenticationStateProvider is not null)
        {
            _authenticationStateProvider.AuthenticationStateChanged -= HandleAuthenticationStateChanged;
        }

        var pendingCallbacks = _pendingCallbacks.ToArray();
        var layoutInterop = _layoutInterop;
        _layoutInterop = null;
        return DisposeCoreAsync(pendingCallbacks, layoutInterop);
    }

    private async Task DisposeCoreAsync(
        IReadOnlyList<Task> pendingCallbacks,
        NavBarLayoutInteropSession? layoutInterop)
    {
        try
        {
            try
            {
                await Task.WhenAll(pendingCallbacks);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            if (layoutInterop is not null)
            {
                await layoutInterop.DisposeAsync();
            }

            _lifetimeCancellation.Dispose();
        }
    }

    private void QueueCallback(Func<Task> callback)
    {
        lock (_disposeSync)
        {
            if (_disposed)
            {
                return;
            }

            _pendingCallbacks.RemoveAll(static task => task.IsCompleted);
            _pendingCallbacks.Add(InvokeAsync(callback));
        }
    }
}
