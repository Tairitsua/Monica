using Microsoft.Extensions.Localization;
using Monica.AI.Abstractions;
using Monica.AI.KnowledgeBase.Facades;
using Monica.AI.KnowledgeBase.Models;
using Monica.AI.UI.Localization;
using Monica.AI.UI.Shared.Support;
using Monica.Core.Results;
using MudBlazor;
using DocumentQueueItemModel = Monica.AI.KnowledgeBase.Models.DocumentQueueItem;
using KnowledgeBaseModel = Monica.AI.KnowledgeBase.Models.KnowledgeBase;

namespace Monica.AI.UI.UIKnowledgeBase.State;

/// <summary>
/// Owns mutable page state and UI orchestration for the knowledge-base management page.
/// </summary>
/// <remarks>Create one instance per visit through <see cref="KnowledgeBaseManagePageStateFactory"/>.
/// The routed page owns asynchronous retirement; facade and UI-service dependencies remain container-owned.</remarks>
public sealed partial class KnowledgeBaseManagePageState : IAsyncDisposable
{
    private readonly KnowledgePageSession _session;
    private long _catalogReadRevision;
    private long _inventoryReadRevision;
    private long _selectedMetadataRevision;
    private readonly KnowledgeBaseFacade _knowledgeBaseFacade;
    private readonly KnowledgeDocumentFacade _knowledgeDocumentFacade;
    private readonly IAIProviderFactory _providerFactory;
    private readonly ISnackbar _snackbar;
    private readonly IStringLocalizer<AIResource> _localizer;

    /// <summary>
    /// Initializes the page state and its collaborators.
    /// </summary>
    public KnowledgeBaseManagePageState(
        KnowledgeBaseFacade knowledgeBaseFacade,
        KnowledgeDocumentFacade knowledgeDocumentFacade,
        IAIProviderFactory providerFactory,
        ISnackbar snackbar,
        IDialogService dialogService,
        IStringLocalizer<AIResource> localizer,
        Func<Action, Task> dispatch)
    {
        _knowledgeBaseFacade = knowledgeBaseFacade;
        _knowledgeDocumentFacade = knowledgeDocumentFacade;
        _providerFactory = providerFactory;
        _snackbar = snackbar;
        _session = new(() => SelectedKnowledgeBase?.Id, dialogService, dispatch);
        _localizer = localizer;
    }

    /// <summary>
    /// Raised when the page should re-render.
    /// </summary>
    public event Func<Task>? StateChanged;

    /// <summary>Whether this visit has stopped admitting operations and publishing renderer work.</summary>
    public bool IsRetired => _session.IsRetired;
    internal long SelectionGeneration => _session.Generation;
    /// <summary>Last catalog read failure; empty after a successful read.</summary>
    public string CatalogErrorMessage { get; private set; } = string.Empty;
    /// <summary>Last selected inventory read failure; empty after a successful read or selection change.</summary>
    public string InventoryErrorMessage { get; private set; } = string.Empty;

    /// <summary>
    /// All knowledge bases shown in the sidebar.
    /// </summary>
    public IReadOnlyList<KnowledgeBaseModel> KnowledgeBases { get; private set; } = [];

    /// <summary>
    /// Current knowledge-base selection.
    /// </summary>
    public KnowledgeBaseModel? SelectedKnowledgeBase { get; private set; }

    /// <summary>
    /// Current document inventory for the selected knowledge base.
    /// </summary>
    public IReadOnlyList<DocumentQueueItemModel> DocumentInventory { get; private set; } = [];

    /// <summary>
    /// Markdown groups available for import.
    /// </summary>
    public IReadOnlyList<string> MarkdownGroups { get; private set; } = [];

    /// <summary>
    /// Markdown group currently selected in the import card.
    /// </summary>
    public string? SelectedMarkdownGroup { get; set; }

    /// <summary>
    /// Whether the first page load is still running.
    /// </summary>
    public bool IsInitialLoading { get; private set; } = true;

    /// <summary>
    /// Whether the current knowledge-base selection is still loading.
    /// </summary>
    public bool IsSelectionLoading { get; private set; }

    /// <summary>
    /// Whether markdown-group metadata was loaded once already.
    /// </summary>
    public bool AreMarkdownGroupsLoaded { get; private set; }

    /// <summary>
    /// Whether markdown groups are currently warming up.
    /// </summary>
    public bool IsMarkdownGroupsLoading { get; private set; }

    /// <summary>
    /// Current loading-phase label shown by the page.
    /// </summary>
    public string LoadingPhaseText { get; private set; } = string.Empty;

    /// <summary>
    /// Current loading progress shown by the page.
    /// </summary>
    public double LoadingProgressValue { get; private set; } = 12;

    /// <summary>
    /// Whether all documents can be removed for the current selection.
    /// </summary>
    public bool CanClearKnowledgeBaseDocuments
        => SelectedKnowledgeBase is not null
           && DocumentInventory.Count > 0
           && DocumentInventory.All(document => document.Status != DocumentStatus.Indexing);

    /// <summary>
    /// Initializes the page for the current visit.
    /// </summary>
    public Task InitializeAsync()
        => _session.RunAsync(() => InitializeAsyncCore(), selectionBound: false);

    private async Task InitializeAsyncCore()
    {
        _session.CheckCurrent();
        IsInitialLoading = true;
        UpdateLoadingState(_localizer["RAG:KnowledgeBase:Title"].Value, 35);
        await LoadKnowledgeBasesAsync();
        _session.CheckCurrent();
        QueueMarkdownGroupWarmup();
        LoadingProgressValue = 100;
        IsInitialLoading = false;
        NotifyStateChanged();
    }

    /// <summary>
    /// Changes the current knowledge-base selection and loads dependent data.
    /// </summary>
    public Task SelectKnowledgeBaseAsync(KnowledgeBaseModel? knowledgeBase)
        => _session.RunAsync(() => SelectKnowledgeBaseAsyncCore(knowledgeBase), selectionBound: false);

    private async Task SelectKnowledgeBaseAsyncCore(KnowledgeBaseModel? knowledgeBase)
    {
        _session.CheckCurrent();
        SelectedKnowledgeBase = knowledgeBase;
        _session.AdvanceSelection();
        IsSelectionLoading = false;
        DocumentInventory = [];
        InventoryErrorMessage = string.Empty;
        if (knowledgeBase is null)
        {
            DocumentInventory = [];
            NotifyStateChanged();
            return;
        }

        IsSelectionLoading = true;
        UpdateLoadingState(_localizer["KnowledgeBase:Documents:Title"].Value, 72);
        NotifyStateChanged();

        await LoadDocumentInventoryAsync();
        _session.CheckCurrent();
        IsSelectionLoading = false;
        LoadingProgressValue = 100;
        QueueMarkdownGroupWarmup();
        NotifyStateChanged();
    }

    private async Task<bool> LoadKnowledgeBasesAsync()
    {
        _session.CheckCurrent();
        var revision = ++_catalogReadRevision;
        var metadataRevision = Interlocked.Increment(ref _selectedMetadataRevision);
        var catalogResult = await _knowledgeBaseFacade.GetAllAsync();
        _session.CheckCurrent();
        if (revision != _catalogReadRevision) return false;
        var error = catalogResult.WithoutData();
        var knowledgeBases = catalogResult.Data;
        if (catalogResult.IsFailure() || knowledgeBases is null)
        {
            CatalogErrorMessage = error.Message ?? _localizer["Common:Error"].Value;
            NotifyStateChanged();
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return false;
        }

        CatalogErrorMessage = string.Empty;
        KnowledgeBases = knowledgeBases.ToList();
        if (SelectedKnowledgeBase is { } selected)
        {
            if (metadataRevision == _selectedMetadataRevision)
            {
                SelectedKnowledgeBase = KnowledgeBases.FirstOrDefault(kb =>
                    string.Equals(kb.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
                if (SelectedKnowledgeBase is null) await SelectKnowledgeBaseAsyncCore(null);
            }
            else
            {
                KnowledgeBases = KnowledgeBases.Select(kb =>
                    string.Equals(kb.Id, selected.Id, StringComparison.OrdinalIgnoreCase) ? selected : kb).ToList();
            }
        }
        NotifyStateChanged();
        return true;
    }

    private async Task<bool> LoadMarkdownGroupsAsync()
    {
        _session.CheckCurrent();
        var groupsResult = await _knowledgeDocumentFacade.GetMarkdownGroupsAsync();
        _session.CheckCurrent();
        var error = groupsResult.WithoutData();
        var groups = groupsResult.Data;
        if (groupsResult.IsFailure() || groups is null)
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return false;
        }

        MarkdownGroups = groups.Select(group => group.Key).ToList();
        AreMarkdownGroupsLoaded = true;
        NotifyStateChanged();
        return true;
    }

    /// <summary>Reloads the selected inventory and persisted base metadata for this selection generation.</summary>
    public Task ReloadSelectedInventoryAsync()
        => _session.RunAsync(LoadDocumentInventoryAsync);

    private async Task LoadDocumentInventoryAsync()
    {
        _session.CheckCurrent();
        var revision = ++_inventoryReadRevision;
        if (SelectedKnowledgeBase is null)
        {
            DocumentInventory = [];
            NotifyStateChanged();
            return;
        }

        var originKnowledgeBaseId = SelectedKnowledgeBase.Id;
        var inventoryResult = await _knowledgeDocumentFacade.GetDocumentInventoryAsync(originKnowledgeBaseId);
        _session.CheckCurrent();
        if (revision != _inventoryReadRevision) return;

        var error = inventoryResult.WithoutData();
        var documents = inventoryResult.Data;
        if (inventoryResult.IsFailure() || documents is null)
        {
            InventoryErrorMessage = error.Message ?? _localizer["Common:Error"].Value;
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            DocumentInventory = [];
        }
        else
        {
            InventoryErrorMessage = string.Empty;
            DocumentInventory = documents.ToList();
            await RefreshSelectedMetadataAsync(originKnowledgeBaseId);
            _session.CheckCurrent();
            if (revision != _inventoryReadRevision) return;
        }

        NotifyStateChanged();
    }

    private async Task RefreshSelectedKnowledgeBaseAsync()
    {
        _session.CheckCurrent();
        if (SelectedKnowledgeBase is null)
        {
            NotifyStateChanged();
            return;
        }

        if (!await LoadKnowledgeBasesAsync()) return;
        _session.CheckCurrent();

        NotifyStateChanged();
    }

    private async Task RefreshSelectedMetadataAsync(string knowledgeBaseId)
    {
        var revision = Interlocked.Increment(ref _selectedMetadataRevision);
        var result = await _knowledgeBaseFacade.GetByIdAsync(knowledgeBaseId);
        _session.CheckCurrent();
        if (revision != _selectedMetadataRevision
            || !string.Equals(SelectedKnowledgeBase?.Id, knowledgeBaseId, StringComparison.OrdinalIgnoreCase)) return;
        var error = result.WithoutData();
        var knowledgeBase = result.Data;
        if (result.IsFailure() || knowledgeBase is null)
        {
            CatalogErrorMessage = error.Message ?? _localizer["Common:Error"].Value;
            return;
        }

        CatalogErrorMessage = string.Empty;
        SelectedKnowledgeBase = knowledgeBase;
        KnowledgeBases = KnowledgeBases.Select(kb =>
            string.Equals(kb.Id, knowledgeBaseId, StringComparison.OrdinalIgnoreCase) ? knowledgeBase : kb).ToList();
    }

    private void UpdateLoadingState(string phaseText, double progressValue)
    {
        LoadingPhaseText = phaseText;
        LoadingProgressValue = progressValue;
        NotifyStateChanged();
    }

    private void QueueMarkdownGroupWarmup()
    {
        if (AreMarkdownGroupsLoaded || IsMarkdownGroupsLoading)
        {
            return;
        }

        IsMarkdownGroupsLoading = true;
        NotifyStateChanged();
        _session.StartBackground(LoadMarkdownGroupsWarmupAsync, selectionBound: false);
    }

    private async Task LoadMarkdownGroupsWarmupAsync()
    {
        _session.CheckCurrent();
        try
        {
            await LoadMarkdownGroupsAsync();
            _session.CheckCurrent();
        }
        finally
        {
            if (!_session.IsRetired)
            {
                IsMarkdownGroupsLoading = false;
                NotifyStateChanged();
            }
        }
    }

    private void NotifyStateChanged()
    {
        _session.StartBackground(async () =>
        {
            if (StateChanged is not { } changed) return;
            foreach (Func<Task> callback in changed.GetInvocationList())
            {
                _session.CheckCurrent();
                await callback();
            }
        });
    }

    /// <summary>Cancels and drains this page's operations and dialogs; borrowed facades remain container-owned.</summary>
    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
