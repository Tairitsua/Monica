using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using MudBlazor;
using Monica.Core.Localization.Models;
using Monica.Core.Results;
using Monica.Markdown.Facades;
using Monica.Markdown.Localization;
using Monica.Markdown.Models;
using Monica.Markdown.UIMarkdown.Dialogs;
using Monica.Markdown.UIMarkdown.Interop;
using Monica.Markdown.UIMarkdown.Models;
using Monica.Modules;
using Monica.UI.Shared.Components.Markdown;
using Monica.Tool.Algorithms.Trees;

namespace Monica.Markdown.UIMarkdown.State;

/// <summary>
/// Creates markdown viewer state whose asynchronous lifetime is owned by the rendering page.
/// </summary>
internal sealed class MarkdownViewerPageStateFactory(
    MarkdownFacade markdownFacade,
    IDialogService dialogService,
    NavigationManager navigationManager,
    ISnackbar snackbar,
    IJSRuntime jsRuntime,
    IStringLocalizer<MarkdownResource> localizer,
    LocalizationProfile localizationProfile)
{
    /// <summary>
    /// Creates and attaches a state instance to its owning component.
    /// </summary>
    internal MarkdownViewerPageState Create(
        Func<Task> renderRequestedAsync,
        Func<Action, Task>? dispatchAsync = null)
    {
        var state = new MarkdownViewerPageState(
            markdownFacade,
            dialogService,
            navigationManager,
            snackbar,
            jsRuntime,
            localizer,
            localizationProfile);

        state.Attach(renderRequestedAsync, dispatchAsync);
        return state;
    }
}

/// <summary>
/// Owns the transient UI state and page orchestration for the markdown viewer route.
/// </summary>
internal sealed class MarkdownViewerPageState(
    MarkdownFacade markdownFacade,
    IDialogService dialogService,
    NavigationManager navigationManager,
    ISnackbar snackbar,
    IJSRuntime jsRuntime,
    IStringLocalizer<MarkdownResource> localizer,
    LocalizationProfile localizationProfile)
    : IAsyncDisposable
{
    private readonly MarkdownViewerOperationLifetime _operations = new();
    private readonly TaskCompletionSource _groupsReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _locationOperationsLock = new();
    private readonly TaskCompletionSource _disposedCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LocalizationProfile _localizationProfile = localizationProfile;
    private readonly DialogOptions _groupSwitcherDialogOptions = new()
    {
        MaxWidth = MaxWidth.Medium,
        FullWidth = true,
        CloseButton = true,
        CloseOnEscapeKey = true
    };

    private readonly DialogOptions _documentSearchDialogOptions = new()
    {
        MaxWidth = MaxWidth.Medium,
        FullWidth = true,
        CloseButton = true,
        CloseOnEscapeKey = true
    };

    private Func<Task>? _renderRequestedAsync;
    private bool _isAttached;
    private MarkdownViewerInteropSession? _interopSession;
    private MarkdownViewerDialogSession? _dialogs;
    private CancellationTokenSource? _activeLocationCancellation;
    private Task? _initializationTask;
    private bool _pendingHeadingTrackerRefresh;
    private PendingSearchNavigation? _pendingSearchNavigation;
    private DocumentRevealRequest? _pendingDocumentReveal;
    private bool _pendingSearchHighlightClear;
    private string? _currentTreeCulture;
    private long _catalogReadVersion;
    private long _locationVersion;
    private bool _ownerObserved;
    private double _ownerWidth;
    private double _ownerHeight;
    private bool _wideSidebarVisible = true;
    private bool _wideOutlineVisible = true;
    private int _disposeRequested;

    /// <summary>
    /// Whether the page is loading its document groups.
    /// </summary>
    public bool IsLoading { get; private set; } = true;

    /// <summary>
    /// Whether the current document content is loading.
    /// </summary>
    public bool IsContentLoading { get; private set; }

    /// <summary>
    /// Whether the sidebar is visible.
    /// </summary>
    public bool ShowSidebar { get; private set; }

    /// <summary>
    /// Whether the table of contents rail is visible.
    /// </summary>
    public bool ShowTableOfContents { get; private set; }

    /// <summary>Whether the measured workspace has room for a persistent library rail.</summary>
    public bool CanPinSidebar => Groups is { Count: > 0 } && _ownerObserved && _ownerWidth >= 1040 && _ownerHeight >= 560;

    /// <summary>Whether the measured workspace has room for a persistent outline with actual headings.</summary>
    public bool CanPinOutline => Groups is { Count: > 0 } && _ownerObserved && _ownerWidth >= 1280 && _ownerHeight >= 560
                                 && DocumentHeadings.Count > 0;

    /// <summary>Whether library visibility represents a temporary native drawer.</summary>
    public bool IsSidebarTemporary => !CanPinSidebar;

    /// <summary>Whether outline visibility represents a temporary native drawer.</summary>
    public bool IsOutlineTemporary => !CanPinOutline;

    /// <summary>The last catalog load failure, distinct from a successfully empty catalog.</summary>
    public string? CatalogError { get; private set; }

    /// <summary>The last active-tree load failure, distinct from an empty tree.</summary>
    public string? TreeError { get; private set; }

    /// <summary>The last selected-document load failure, distinct from an empty markdown file.</summary>
    public string? ContentError { get; private set; }

    /// <summary>
    /// All registered markdown groups.
    /// </summary>
    public List<MarkdownDocumentGroup>? Groups { get; private set; }

    /// <summary>
    /// The currently selected group key.
    /// </summary>
    public string? SelectedGroupKey { get; private set; }

    /// <summary>
    /// The currently selected document culture.
    /// </summary>
    public string? SelectedCulture { get; private set; }

    /// <summary>
    /// The loaded tree for the active group.
    /// </summary>
    public TreeNode<MarkdownDocumentNodeData>? CurrentTree { get; private set; }

    /// <summary>
    /// The currently selected document.
    /// </summary>
    public MarkdownDocument? SelectedDocument { get; private set; }

    /// <summary>
    /// The raw markdown content of the selected document.
    /// </summary>
    public string? DocumentContent { get; private set; }

    /// <summary>
    /// The headings reported by the markdown renderer.
    /// </summary>
    public IReadOnlyList<MoMarkdownHeading> DocumentHeadings { get; private set; } = [];

    /// <summary>
    /// The current anchor id reflected by navigation or heading observation.
    /// </summary>
    public string? CurrentAnchorId { get; private set; }

    /// <summary>
    /// Persistent state describing a document missing from the selected language.
    /// </summary>
    public MarkdownMissingTranslationState? MissingTranslation { get; private set; }

    /// <summary>
    /// The active group resolved from the current selection.
    /// </summary>
    public MarkdownDocumentGroup? CurrentGroup =>
        Groups?.FirstOrDefault(group => string.Equals(group.Key, SelectedGroupKey, StringComparison.Ordinal));

    /// <summary>
    /// The resolved active language within the current group.
    /// </summary>
    public MarkdownDocumentLanguage? CurrentLanguage =>
        CurrentGroup?.ResolveLanguage(SelectedCulture);

    /// <summary>
    /// Languages available for the current group.
    /// </summary>
    public IReadOnlyList<MarkdownDocumentLanguage> CurrentLanguages =>
        CurrentGroup?.Languages ?? Array.Empty<MarkdownDocumentLanguage>();

    /// <summary>
    /// Whether the current group should expose the language switcher.
    /// </summary>
    public bool ShowLanguageSwitcher =>
        CurrentGroup is { IsMultilingual: true } group && group.Languages.Count > 1;

    /// <summary>
    /// Attaches the page render callback and starts observing navigation changes.
    /// </summary>
    public void Attach(Func<Task> renderRequestedAsync, Func<Action, Task>? dispatchAsync = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposeRequested, this);

        _renderRequestedAsync = renderRequestedAsync;

        if (_isAttached)
        {
            return;
        }

        navigationManager.LocationChanged += OnLocationChanged;
        _interopSession = new MarkdownViewerInteropSession(jsRuntime, OnHashChangedAsync, OnViewportChangedAsync);
        _dialogs = new MarkdownViewerDialogSession(dialogService, dispatchAsync ?? (action =>
        {
            action();
            return Task.CompletedTask;
        }));
        _isAttached = true;
    }

    /// <summary>
    /// Loads non-DOM page data and queues the current route through the navigation coordinator.
    /// </summary>
    public Task InitializeAsync()
    {
        if (IsDisposeRequested)
        {
            return Task.CompletedTask;
        }

        return _initializationTask ??= _operations.RunAsync(InitializeCoreAsync);
    }

    /// <summary>
    /// Initializes and synchronizes DOM-bound behavior after the viewer has rendered.
    /// </summary>
    public Task HandleAfterRenderAsync(ElementReference viewerRoot, bool firstRender) =>
        _operations.RunAsync(_ => HandleAfterRenderCoreAsync(viewerRoot, firstRender));

    private async Task HandleAfterRenderCoreAsync(ElementReference viewerRoot, bool firstRender)
    {
        if (IsDisposeRequested || _interopSession is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(viewerRoot.Id))
        {
            await _interopSession.InitializeAsync(viewerRoot);
        }

        if (_pendingHeadingTrackerRefresh)
        {
            await RefreshHeadingTrackerAsync();
        }

        if (_pendingSearchHighlightClear)
        {
            await ClearSearchHitAsync();
        }

        if (_pendingSearchNavigation is not null && CanApplySearchNavigation(_pendingSearchNavigation))
        {
            await ApplySearchHitAsync(_pendingSearchNavigation);
        }
        if (_pendingDocumentReveal is { } reveal && CanRevealDocument(reveal))
        {
            _pendingDocumentReveal = null;
            var document = SelectedDocument!;
            await _interopSession!.RevealDocumentHeadingAsync(document.GroupKey, document.RelativePath, document.Culture);
        }
    }

    /// <summary>
    /// Opens the knowledge base switcher dialog.
    /// </summary>
    public Task OpenGroupSwitcherAsync() => _operations.RunAsync(_ => OpenGroupSwitcherCoreAsync());

    private async Task OpenGroupSwitcherCoreAsync()
    {
        if (IsDisposeRequested || Groups is not { Count: > 0 } groups)
        {
            return;
        }
        CloseTemporaryPanes();
        await NotifyStateChangedAsync();
        if (IsDisposeRequested) return;

        var parameters = new DialogParameters<DocumentGroupSwitcherDialog>
        {
            { x => x.Groups, groups },
            { x => x.SelectedGroupKey, SelectedGroupKey }
        };

        var originVersion = _locationVersion;
        var originUri = navigationManager.Uri;
        var result = await _dialogs!.ShowAsync<DocumentGroupSwitcherDialog>(
            localizer["MarkdownViewer:Dialogs:SwitchKnowledgeBaseTitle"],
            parameters,
            _groupSwitcherDialogOptions);

        if (IsDisposeRequested) return;

        // The dialog may have refreshed even when it was canceled or selected the same group.
        // Its actual async work is drained before reading the catalog's new snapshot.
        var selectedGroupKey = originVersion == _locationVersion
            && result is { Canceled: false, Data: string key } ? key : null;
        await ReloadAsync();
        if (!IsDisposeRequested && selectedGroupKey is not null
            && string.Equals(originUri, navigationManager.Uri, StringComparison.Ordinal)
            && !string.Equals(selectedGroupKey, SelectedGroupKey, StringComparison.Ordinal))
            await OnGroupSelectedAsync(selectedGroupKey);
    }

    /// <summary>
    /// Opens the document search dialog.
    /// </summary>
    public Task OpenDocumentSearchAsync() => _operations.RunAsync(_ => OpenDocumentSearchCoreAsync());

    private async Task OpenDocumentSearchCoreAsync()
    {
        if (IsDisposeRequested || Groups is not { Count: > 0 })
        {
            return;
        }
        CloseTemporaryPanes();
        await NotifyStateChangedAsync();
        if (IsDisposeRequested) return;

        var parameters = new DialogParameters<DocumentSearchDialog>
        {
            { x => x.CurrentGroup, CurrentGroup },
            { x => x.CurrentCulture, SelectedCulture }
        };

        var originCulture = SelectedCulture;
        var originVersion = _locationVersion;
        var result = await _dialogs!.ShowAsync<DocumentSearchDialog>(
            localizer["MarkdownViewer:Dialogs:Search:Title"],
            parameters,
            _documentSearchDialogOptions);

        if (IsDisposeRequested)
        {
            return;
        }

        if (IsDisposeRequested || originVersion != _locationVersion
            || result is not { Canceled: false, Data: MarkdownDocumentSearchResult searchResult })
        {
            return;
        }

        await OpenSearchResultAsync(searchResult, originCulture);
    }

    /// <summary>Reads a fresh catalog snapshot and reloads the latest route even when its URL is unchanged.</summary>
    public Task ReloadAsync() => _operations.RunAsync(async token =>
    {
        CancelActiveLocation();
        if (await LoadGroupsAsync(token))
        {
            _groupsReady.TrySetResult();
            await QueueLocation(MarkdownViewerLocation.FromAbsoluteUri(navigationManager.Uri), forceReload: true);
        }
    });

    /// <summary>
    /// Handles a new group selection from the sidebar or switcher dialog.
    /// </summary>
    public Task OnGroupSelectedAsync(string groupKey)
    {
        if (IsDisposeRequested)
        {
            return Task.CompletedTask;
        }

        if (string.Equals(groupKey, SelectedGroupKey, StringComparison.Ordinal)) return ReloadAsync();

        ResetSearchHighlight();
        _pendingDocumentReveal = null;
        ClearMissingTranslation();
        CurrentAnchorId = null;
        DocumentHeadings = [];
        ShowTableOfContents = false;
        _pendingHeadingTrackerRefresh = true;
        var targetGroup = ResolveTargetGroup(groupKey);
        var targetCulture = ResolveGroupCulture(targetGroup, SelectedCulture);
        if (targetGroup?.IsMultilingual == true)
        {
            SelectedCulture = targetCulture;
        }

        NavigateToLocation(new MarkdownViewerLocation(groupKey, Culture: SelectedCulture));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles a document selection from the document tree.
    /// </summary>
    public Task OnDocumentSelectedAsync(MarkdownDocument? document)
    {
        if (IsDisposeRequested)
        {
            return Task.CompletedTask;
        }

        ResetSearchHighlight();
        _pendingDocumentReveal = IsSidebarTemporary && ShowSidebar && document is not null
            ? new(document.GroupKey, document.RelativePath, document.NavigationRelativePath, document.Culture, _locationVersion)
            : null;
        if (IsSidebarTemporary) SetSidebarOpen(false);
        ClearMissingTranslation();
        CurrentAnchorId = null;
        _pendingHeadingTrackerRefresh = true;
        NavigateToLocation(new MarkdownViewerLocation(
            SelectedGroupKey,
            document?.NavigationRelativePath,
            Culture: SelectedCulture));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles switching to another document language within the current group.
    /// </summary>
    public Task OnLanguageSelectedAsync(string culture)
    {
        if (IsDisposeRequested
            || CurrentGroup is not { IsMultilingual: true }
            || string.IsNullOrWhiteSpace(culture)
            || string.Equals(SelectedCulture, culture, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        ResetSearchHighlight();
        _pendingDocumentReveal = null;
        var targetAnchorId = SelectedDocument is not null ? CurrentAnchorId : null;
        CurrentAnchorId = null;
        DocumentHeadings = [];
        ShowTableOfContents = false;
        _pendingHeadingTrackerRefresh = true;

        var targetDocumentRelativePath =
            SelectedDocument?.NavigationRelativePath
            ?? MissingTranslation?.DocumentRelativePath;

        NavigateToLocation(new MarkdownViewerLocation(
            SelectedGroupKey,
            targetDocumentRelativePath,
            targetAnchorId,
            culture));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Toggles the sidebar rail.
    /// </summary>
    public void ToggleSidebar()
    {
        if (IsDisposeRequested)
        {
            return;
        }

        SetSidebarOpen(!ShowSidebar);
    }

    /// <summary>
    /// Toggles the table of contents rail.
    /// </summary>
    public void ToggleTableOfContents()
    {
        if (IsDisposeRequested)
        {
            return;
        }

        SetOutlineOpen(!ShowTableOfContents);
    }

    /// <summary>Sets native library visibility, retaining an independent preference for a persistent rail.</summary>
    public void SetSidebarOpen(bool open)
    {
        if (IsDisposeRequested) return;
        ShowSidebar = open && Groups is { Count: > 0 };
        if (CanPinSidebar) _wideSidebarVisible = open;
        else if (open && IsOutlineTemporary) ShowTableOfContents = false;
    }

    /// <summary>Sets native outline visibility, retaining an independent preference for a persistent rail.</summary>
    public void SetOutlineOpen(bool open)
    {
        if (IsDisposeRequested) return;
        ShowTableOfContents = open && Groups is { Count: > 0 } && DocumentHeadings.Count > 0;
        if (CanPinOutline) _wideOutlineVisible = open;
        else if (open && IsSidebarTemporary) ShowSidebar = false;
    }

    /// <summary>Applies the observed workspace content box, after shell, page gutters, and toolbar sizing.</summary>
    public Task OnViewportChangedAsync(double width, double height) => _operations.RunAsync(async _ =>
    {
        if (IsDisposeRequested || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return;
        var sidebarWasPinned = CanPinSidebar;
        var outlineWasPinned = CanPinOutline;
        var wasObserved = _ownerObserved;
        _ownerWidth = width;
        _ownerHeight = height;
        _ownerObserved = true;
        if (!wasObserved || sidebarWasPinned != CanPinSidebar)
            ShowSidebar = CanPinSidebar && _wideSidebarVisible;
        if (!wasObserved || outlineWasPinned != CanPinOutline)
            ShowTableOfContents = CanPinOutline && _wideOutlineVisible;
        if (wasObserved && (sidebarWasPinned != CanPinSidebar || outlineWasPinned != CanPinOutline))
            CloseTemporaryPanes();
        await NotifyStateChangedAsync();
    });

    /// <summary>
    /// Updates the heading list reported by the markdown renderer.
    /// </summary>
    public Task OnDocumentHeadingsChangedAsync(IReadOnlyList<MoMarkdownHeading> headings)
    {
        if (IsDisposeRequested || Groups is not { Count: > 0 } || SelectedDocument is null || DocumentContent is null)
        {
            return Task.CompletedTask;
        }

        var normalizedHeadings = headings
            .Where(static heading => heading.Level <= 3)
            .ToArray();

        if (DocumentHeadings.SequenceEqual(normalizedHeadings))
        {
            return Task.CompletedTask;
        }

        var outlineWasPinned = CanPinOutline;
        DocumentHeadings = normalizedHeadings;
        if (outlineWasPinned != CanPinOutline)
            ShowTableOfContents = CanPinOutline && _wideOutlineVisible;
        if (DocumentHeadings.Count == 0) ShowTableOfContents = false;
        CurrentAnchorId = ResolveCurrentAnchorId(CurrentAnchorId);
        _pendingHeadingTrackerRefresh = true;
        return NotifyStateChangedAsync();
    }

    /// <summary>
    /// Navigates to a heading inside the active document.
    /// </summary>
    public Task OnHeadingSelectedAsync(string anchorId)
    {
        if (IsDisposeRequested
            || SelectedDocument is null
            || string.IsNullOrWhiteSpace(anchorId))
        {
            return Task.CompletedTask;
        }

        CurrentAnchorId = anchorId;
        if (IsOutlineTemporary) SetOutlineOpen(false);
        NavigateToLocation(new MarkdownViewerLocation(
            SelectedGroupKey,
            SelectedDocument.NavigationRelativePath,
            anchorId,
            SelectedCulture));
        return NotifyStateChangedAsync();
    }

    /// <summary>
    /// Receives hash updates from this viewer's instance-scoped JavaScript session.
    /// </summary>
    public Task OnHashChangedAsync(string? anchorId)
    {
        if (IsDisposeRequested)
        {
            return Task.CompletedTask;
        }

        var normalizedAnchorId = ResolveCurrentAnchorId(anchorId);
        if (string.Equals(CurrentAnchorId, normalizedAnchorId, StringComparison.Ordinal))
        {
            return NotifyStateChangedAsync();
        }

        CurrentAnchorId = normalizedAnchorId;
        return NotifyStateChangedAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            await _disposedCompletion.Task;
            return;
        }

        try
        {
            _renderRequestedAsync = null;

            if (_isAttached)
            {
                navigationManager.LocationChanged -= OnLocationChanged;
                _isAttached = false;
            }

            CancelActiveLocation();
            _groupsReady.TrySetCanceled(_operations.Token);
            var interopSession = _interopSession;
            _interopSession = null;
            var dialogs = _dialogs;
            _dialogs = null;

            // Initiate every owner's retirement before drainage. In particular, native Result
            // and queued JavaScript callbacks must be released while page producers are draining.
            var operationRetirement = _operations.RetireAsync().AsTask();
            var dialogRetirement = dialogs?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            var interopRetirement = interopSession?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            await Task.WhenAll(operationRetirement, dialogRetirement, interopRetirement);
            _disposedCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposedCompletion.TrySetException(exception);
            throw;
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadGroupsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _groupsReady.TrySetResult();
            _ = QueueLocation(MarkdownViewerLocation.FromAbsoluteUri(navigationManager.Uri));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> LoadGroupsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readVersion = Interlocked.Increment(ref _catalogReadVersion);
        IsLoading = true;
        CatalogError = null;
        await NotifyStateChangedAsync();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await markdownFacade.GetAllDocumentGroupsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposeRequested || readVersion != _catalogReadVersion) return false;

            if (response.IsFailure())
            {
                CatalogError = response.Message ?? string.Empty;
                snackbar.Add(CatalogError, Severity.Error);
                return false;
            }

            var groups = response.Data ?? throw new InvalidOperationException("The document group read returned no data.");
            var sidebarWasPinned = CanPinSidebar;
            Groups = groups;
            if (sidebarWasPinned != CanPinSidebar) ShowSidebar = CanPinSidebar && _wideSidebarVisible;
            if (groups.Count == 0)
            {
                ShowSidebar = false;
                ShowTableOfContents = false;
                _pendingDocumentReveal = null;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposeRequested && readVersion == _catalogReadVersion)
            {
                CatalogError = ex.Message;
                snackbar.Add(localizer["MarkdownViewer:Messages:LoadDataFailed", ex.Message], Severity.Error);
            }
        }
        finally
        {
            if (!IsDisposeRequested && readVersion == _catalogReadVersion)
            {
                IsLoading = false;
                await NotifyStateChangedAsync();
            }
        }
        return false;
    }

    private Task OpenSearchResultAsync(MarkdownDocumentSearchResult result, string? originCulture)
    {
        if (IsDisposeRequested)
        {
            return Task.CompletedTask;
        }

        var resultGroup = Groups?.FirstOrDefault(group =>
            string.Equals(group.Key, result.GroupKey, StringComparison.Ordinal));
        if (resultGroup is null) return Task.CompletedTask;
        var resultCulture = resultGroup.IsMultilingual
            ? resultGroup.ResolvePreferredLanguage(originCulture) : null;
        var navigation = new PendingSearchNavigation(result, resultCulture);
        _pendingSearchNavigation = navigation;
        _pendingSearchHighlightClear = false;

        var location = new MarkdownViewerLocation(
            result.GroupKey,
            result.DocumentRelativePath,
            result.AnchorId,
            resultCulture ?? originCulture);
        var targetUri = location.ToRelativeUri();
        var currentUri = "/" + navigationManager.ToBaseRelativePath(navigationManager.Uri);

        if (string.Equals(targetUri, currentUri, StringComparison.Ordinal))
        {
            _ = QueueLocation(location);
            return Task.CompletedTask;
        }

        NavigateToLocation(location);
        return Task.CompletedTask;
    }

    private async Task ApplyLocationAsync(
        MarkdownViewerLocation location,
        CancellationToken cancellationToken,
        bool forceReload)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestedAnchorId = ResolveCurrentAnchorId(location.AnchorId);
        CurrentAnchorId = requestedAnchorId;
        SelectedCulture = ResolveGlobalCulturePreference(location.Culture);

        if (Groups is not { Count: > 0 })
        {
            if (CatalogError is null)
            {
                SelectedGroupKey = null;
                CurrentTree = null;
                TreeError = null;
                ClearMissingTranslation();
                ClearDocumentSelection();
                ResetSearchHighlight();
                await NotifyStateChangedAsync();
            }
            return;
        }

        var targetGroup = ResolveTargetGroup(location.GroupKey);
        if (targetGroup is null)
        {
            ResetSearchHighlight();
            ClearMissingTranslation();
            ClearDocumentSelection();
            await NotifyStateChangedAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var activeCulture = ResolveGroupCulture(targetGroup, location.Culture);
        if (targetGroup.IsMultilingual)
        {
            SelectedCulture = activeCulture;
        }

        if (_pendingSearchNavigation is { } pendingSearch
            && (!string.Equals(pendingSearch.Result.GroupKey, targetGroup.Key, StringComparison.Ordinal)
                || !string.Equals(pendingSearch.Result.DocumentRelativePath, location.DocumentRelativePath,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(pendingSearch.Culture, targetGroup.IsMultilingual ? activeCulture : null,
                    StringComparison.OrdinalIgnoreCase)))
            ResetSearchHighlight();

        if (forceReload || !string.Equals(SelectedGroupKey, targetGroup.Key, StringComparison.Ordinal)
            || CurrentTree is null
            || !string.Equals(_currentTreeCulture, activeCulture, StringComparison.OrdinalIgnoreCase))
        {
            await LoadGroupTreeAsync(
                targetGroup.Key,
                activeCulture,
                requestedAnchorId,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (TreeError is not null) return;
        }

        if (targetGroup.HasConfigurationError)
        {
            ClearMissingTranslation();
            ClearDocumentSelection();
            await NotifyStateChangedAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if (string.IsNullOrWhiteSpace(location.DocumentRelativePath))
        {
            ResetSearchHighlight();
            ClearMissingTranslation();
            ClearDocumentSelection();
            await NotifyStateChangedAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var targetDocument = targetGroup.FindDocument(location.DocumentRelativePath, activeCulture);
        if (targetDocument is null)
        {
            ResetSearchHighlight();
            ClearDocumentSelection();

            if (targetGroup.IsMultilingual && !string.IsNullOrWhiteSpace(activeCulture))
            {
                SetMissingTranslation(location.DocumentRelativePath, activeCulture);
            }
            else
            {
                ClearMissingTranslation();
            }

            await NotifyStateChangedAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        ClearMissingTranslation();

        var isCurrentDocument = SelectedDocument is not null
                                && string.Equals(SelectedDocument.GroupKey, targetDocument.GroupKey, StringComparison.Ordinal)
                                && string.Equals(SelectedDocument.RelativePath, targetDocument.RelativePath, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(SelectedDocument.Culture, targetDocument.Culture, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(SelectedDocument.FilePath, targetDocument.FilePath, StringComparison.OrdinalIgnoreCase);

        if (!isCurrentDocument || DocumentContent is null)
        {
            DocumentHeadings = [];
            ShowTableOfContents = false;
            await LoadDocumentAsync(targetDocument, requestedAnchorId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        CurrentAnchorId = requestedAnchorId;
        await NotifyStateChangedAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task LoadGroupTreeAsync(
        string groupKey,
        string? culture,
        string? requestedAnchorId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SelectedGroupKey = groupKey;
        ClearDocumentSelection();
        ClearMissingTranslation();
        CurrentAnchorId = requestedAnchorId;
        CurrentTree = null;
        TreeError = null;
        _currentTreeCulture = null;

        var group = CurrentGroup;
        if (group is null || group.HasConfigurationError)
        {
            await NotifyStateChangedAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var response = await markdownFacade.GetDocumentTreeAsync(groupKey, culture);
        cancellationToken.ThrowIfCancellationRequested();

        if (response.IsFailure())
        {
            TreeError = response.Message ?? string.Empty;
            snackbar.Add(TreeError, Severity.Error);
            await NotifyStateChangedAsync();
            return;
        }

        CurrentTree = response.Data;
        _currentTreeCulture = group.IsMultilingual ? culture : null;
        await NotifyStateChangedAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task LoadDocumentAsync(
        MarkdownDocument? document,
        string? requestedAnchorId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SelectedDocument = document;
        DocumentContent = null;
        ContentError = null;
        CurrentAnchorId = requestedAnchorId;

        if (document is null)
        {
            IsContentLoading = false;
            await NotifyStateChangedAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        IsContentLoading = true;
        await NotifyStateChangedAsync();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var response = await markdownFacade.GetDocumentContentAsync(document);
            cancellationToken.ThrowIfCancellationRequested();

            if (response.IsFailure())
            {
                ContentError = response.Message ?? string.Empty;
                snackbar.Add(ContentError, Severity.Error);
                return;
            }

            DocumentContent = response.Data;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && !IsDisposeRequested)
            {
                IsContentLoading = false;
                await NotifyStateChangedAsync();
            }
        }
    }

    private MarkdownDocumentGroup? ResolveTargetGroup(string? groupKey)
    {
        if (Groups is not { Count: > 0 })
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(groupKey))
        {
            var matchedGroup = Groups.FirstOrDefault(group =>
                string.Equals(group.Key, groupKey, StringComparison.Ordinal));
            if (matchedGroup is not null)
            {
                return matchedGroup;
            }
        }

        if (!string.IsNullOrWhiteSpace(SelectedGroupKey) && CurrentGroup is not null)
        {
            return CurrentGroup;
        }

        return Groups.FirstOrDefault(static group => !group.HasConfigurationError)
               ?? Groups.FirstOrDefault(static group => group.IsValid)
               ?? Groups[0];
    }

    private string? ResolveGroupCulture(MarkdownDocumentGroup? group, string? requestedCulture)
    {
        if (group is not { IsMultilingual: true })
        {
            return SelectedCulture;
        }

        return group.ResolvePreferredLanguage(
            requestedCulture,
            SelectedCulture,
            CultureInfo.CurrentUICulture.Name,
            _localizationProfile.DefaultCulture);
    }

    private string? ResolveGlobalCulturePreference(string? requestedCulture)
    {
        return _localizationProfile.ResolveCulture(
            requestedCulture,
            SelectedCulture,
            CultureInfo.CurrentUICulture.Name);
    }

    private void SetMissingTranslation(string documentRelativePath, string targetCulture)
    {
        MissingTranslation = new MarkdownMissingTranslationState(documentRelativePath, targetCulture);
    }

    private void ClearMissingTranslation()
    {
        MissingTranslation = null;
    }

    private void ClearDocumentSelection()
    {
        SelectedDocument = null;
        DocumentContent = null;
        ContentError = null;
        IsContentLoading = false;
        CurrentAnchorId = null;
        DocumentHeadings = [];
        ShowTableOfContents = false;
        _pendingHeadingTrackerRefresh = true;
    }

    private async Task RefreshHeadingTrackerAsync()
    {
        if (IsDisposeRequested || _interopSession is null)
        {
            return;
        }

        _pendingHeadingTrackerRefresh = false;
        var headingIds = SelectedDocument is null
            ? Array.Empty<string>()
            : DocumentHeadings.Select(static heading => heading.Id).ToArray();

        await _interopSession.RefreshHeadingTrackerAsync(headingIds, CurrentAnchorId);
    }

    private void ResetSearchHighlight()
    {
        _pendingSearchNavigation = null;
        _pendingSearchHighlightClear = true;
    }

    private bool CanApplySearchNavigation(PendingSearchNavigation navigation)
    {
        return !IsDisposeRequested
               && _interopSession is not null
               && !IsContentLoading
               && SelectedDocument is not null
               && DocumentContent is not null
               && string.Equals(SelectedDocument.GroupKey, navigation.Result.GroupKey, StringComparison.Ordinal)
               && string.Equals(SelectedDocument.Culture, navigation.Culture, StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   SelectedDocument.NavigationRelativePath,
                   navigation.Result.DocumentRelativePath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private bool CanRevealDocument(DocumentRevealRequest request) => !IsDisposeRequested
        && _interopSession is not null && request.LocationVersion == _locationVersion
        && !IsContentLoading && ContentError is null && DocumentContent is not null
        && SelectedDocument is { } document
        && string.Equals(document.GroupKey, request.GroupKey, StringComparison.Ordinal)
        && string.Equals(document.RelativePath, request.RelativePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(document.Culture, request.Culture, StringComparison.OrdinalIgnoreCase);

    private void CloseTemporaryPanes()
    {
        if (IsSidebarTemporary) SetSidebarOpen(false);
        if (IsOutlineTemporary) SetOutlineOpen(false);
    }

    private async Task ApplySearchHitAsync(PendingSearchNavigation navigation)
    {
        if (IsDisposeRequested || _interopSession is null)
        {
            return;
        }

        _pendingSearchNavigation = null;
        await _interopSession.HighlightSearchHitAsync(navigation.Result.Locator);
    }

    private async Task ClearSearchHitAsync()
    {
        if (IsDisposeRequested || _interopSession is null)
        {
            return;
        }

        _pendingSearchHighlightClear = false;
        await _interopSession.ClearSearchHitAsync();
    }

    private string? ResolveCurrentAnchorId(string? anchorId)
    {
        var normalizedAnchorId = NormalizeAnchorId(anchorId);
        if (normalizedAnchorId is null || DocumentHeadings.Count == 0)
        {
            return normalizedAnchorId;
        }

        foreach (var candidate in GetAnchorCandidates(normalizedAnchorId))
        {
            var matchedHeading = DocumentHeadings.FirstOrDefault(
                heading => string.Equals(heading.Id, candidate, StringComparison.Ordinal));
            if (matchedHeading is not null)
            {
                return matchedHeading.Id;
            }
        }

        return normalizedAnchorId;
    }

    private static IEnumerable<string> GetAnchorCandidates(string anchorId)
    {
        yield return anchorId;

        string? decodedAnchorId = null;
        try
        {
            decodedAnchorId = Uri.UnescapeDataString(anchorId);
        }
        catch (UriFormatException)
        {
        }

        if (!string.IsNullOrEmpty(decodedAnchorId)
            && !string.Equals(decodedAnchorId, anchorId, StringComparison.Ordinal))
        {
            yield return decodedAnchorId;

            var encodedDecodedAnchorId = WebUtility.UrlEncode(decodedAnchorId);
            if (!string.Equals(encodedDecodedAnchorId, anchorId, StringComparison.Ordinal))
            {
                yield return encodedDecodedAnchorId;
            }
        }
    }

    private static string? NormalizeAnchorId(string? anchorId)
    {
        if (string.IsNullOrWhiteSpace(anchorId))
        {
            return null;
        }

        var normalizedAnchorId = anchorId.Trim();
        if (normalizedAnchorId.StartsWith('#'))
        {
            normalizedAnchorId = normalizedAnchorId[1..];
        }

        return string.IsNullOrWhiteSpace(normalizedAnchorId) ? null : normalizedAnchorId;
    }

    private void NavigateToLocation(MarkdownViewerLocation location)
    {
        if (IsDisposeRequested)
        {
            return;
        }

        var targetUri = location.ToRelativeUri();
        var currentUri = "/" + navigationManager.ToBaseRelativePath(navigationManager.Uri);

        if (string.Equals(targetUri, currentUri, StringComparison.Ordinal))
        {
            return;
        }

        navigationManager.NavigateTo(targetUri);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        _ = QueueLocation(MarkdownViewerLocation.FromAbsoluteUri(e.Location));
    }

    private Task QueueLocation(MarkdownViewerLocation location, bool forceReload = false)
    {
        if (IsDisposeRequested)
        {
            return Task.CompletedTask;
        }

        CancellationTokenSource? previousCancellation;
        CancellationTokenSource operationCancellation;
        lock (_locationOperationsLock)
        {
            if (IsDisposeRequested)
            {
                return Task.CompletedTask;
            }

            previousCancellation = _activeLocationCancellation;
            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_operations.Token);
            _activeLocationCancellation = operationCancellation;
            ++_locationVersion;
            if (_pendingDocumentReveal is { } reveal)
            {
                var targetGroup = Groups?.FirstOrDefault(group =>
                    string.Equals(group.Key, location.GroupKey, StringComparison.Ordinal));
                var culture = targetGroup?.IsMultilingual == true
                    ? targetGroup.ResolvePreferredLanguage(location.Culture) : null;
                _pendingDocumentReveal = string.Equals(reveal.GroupKey, location.GroupKey, StringComparison.Ordinal)
                    && string.Equals(reveal.NavigationPath, location.DocumentRelativePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(reveal.Culture, culture, StringComparison.OrdinalIgnoreCase)
                    ? reveal with { LocationVersion = _locationVersion } : null;
            }
        }
        CancelLocation(previousCancellation);
        if (_operations.TryRunAsync(_ => ProcessLocationAsync(location, operationCancellation, forceReload), out var operation))
            return operation;
        lock (_locationOperationsLock)
        {
            if (ReferenceEquals(_activeLocationCancellation, operationCancellation))
                _activeLocationCancellation = null;
        }
        operationCancellation.Dispose();
        return Task.CompletedTask;
    }

    private async Task ProcessLocationAsync(
        MarkdownViewerLocation location,
        CancellationTokenSource operationCancellation,
        bool forceReload)
    {
        try
        {
            await _groupsReady.Task.WaitAsync(operationCancellation.Token);
            await ApplyLocationAsync(location, operationCancellation.Token, forceReload);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposeRequested && !operationCancellation.IsCancellationRequested)
            {
                snackbar.Add(localizer["MarkdownViewer:Messages:LoadDataFailed", ex.Message], Severity.Error);
            }
        }
        finally
        {
            lock (_locationOperationsLock)
            {
                if (ReferenceEquals(_activeLocationCancellation, operationCancellation))
                {
                    _activeLocationCancellation = null;
                }
            }

            operationCancellation.Dispose();
        }
    }

    private Task NotifyStateChangedAsync() => _operations.RunAsync(_ =>
        IsDisposeRequested ? Task.CompletedTask : _renderRequestedAsync?.Invoke() ?? Task.CompletedTask);

    private void CancelActiveLocation()
    {
        CancellationTokenSource? cancellation;
        lock (_locationOperationsLock) cancellation = _activeLocationCancellation;
        CancelLocation(cancellation);
    }

    private static void CancelLocation(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // A producer can finish and dispose its source after the cancellation reference was captured.
        }
    }

    private bool IsDisposeRequested => Volatile.Read(ref _disposeRequested) != 0;

    private sealed record PendingSearchNavigation(MarkdownDocumentSearchResult Result, string? Culture);
    private sealed record DocumentRevealRequest(
        string GroupKey, string RelativePath, string NavigationPath, string? Culture, long LocationVersion);
}
