using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Markdown.Facades;
using Monica.Markdown.Models;
using Monica.Markdown.UIMarkdown.Models;
using Monica.Modules;

namespace Monica.Markdown.UIMarkdown.State;

/// <summary>
/// Creates search state whose asynchronous lifetime is owned by the rendering dialog.
/// </summary>
internal sealed class MarkdownDocumentSearchStateFactory(
    MarkdownFacade markdownFacade,
    IOptions<ModuleMarkdownOption> markdownOptions,
    IOptions<ModuleMarkdownUIOption> markdownUiOptions)
{
    /// <summary>
    /// Creates state for one document search dialog.
    /// </summary>
    internal MarkdownDocumentSearchState Create(
        MarkdownDocumentGroup? currentGroup,
        string? currentCulture)
    {
        return new MarkdownDocumentSearchState(
            markdownFacade,
            markdownOptions,
            markdownUiOptions,
            currentGroup,
            currentCulture);
    }
}

/// <summary>
/// Owns the cancellable search work for one markdown document search dialog.
/// </summary>
internal sealed class MarkdownDocumentSearchState(
    MarkdownFacade markdownFacade,
    IOptions<ModuleMarkdownOption> markdownOptions,
    IOptions<ModuleMarkdownUIOption> markdownUiOptions,
    MarkdownDocumentGroup? currentGroup,
    string? currentCulture)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Lock _searchLock = new();
    private readonly List<Task> _searchOperations = [];
    private readonly TaskCompletionSource _disposedCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ModuleMarkdownOption _markdownOption = markdownOptions.Value;
    private readonly ModuleMarkdownUIOption _markdownUiOption = markdownUiOptions.Value;

    private CancellationTokenSource? _activeSearchCancellation;
    private int _disposeRequested;

    /// <summary>
    /// The knowledge base currently selected when the dialog opens.
    /// </summary>
    public MarkdownDocumentGroup? CurrentGroup { get; private set; } = currentGroup;

    /// <summary>
    /// The active document culture when the dialog opens.
    /// </summary>
    public string? CurrentCulture { get; private set; } = currentCulture;

    /// <summary>
    /// The current raw query text.
    /// </summary>
    public string Query { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the search should include every knowledge base.
    /// </summary>
    public bool IncludeAllKnowledgeBases { get; private set; }

    /// <summary>
    /// Whether a search request is currently in flight.
    /// </summary>
    public bool IsSearching { get; private set; }

    /// <summary>
    /// The latest search error, if one occurred.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// The latest search results.
    /// </summary>
    public IReadOnlyList<MarkdownDocumentSearchResult> Results { get; private set; } = [];

    /// <summary>
    /// Minimum query length required before search execution.
    /// </summary>
    public int MinimumQueryLength => Math.Max(1, _markdownOption.DocumentSearchMinQueryLength);

    /// <summary>
    /// Debounce interval used by the dialog input.
    /// </summary>
    public double SearchDebounceMilliseconds =>
        Math.Max(0, _markdownUiOption.DocumentSearchDebounceMilliseconds);

    /// <summary>
    /// Whether the dialog currently has a non-empty query.
    /// </summary>
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(Query);

    /// <summary>
    /// Whether the current query satisfies the minimum search length.
    /// </summary>
    public bool HasEnoughQueryLength => Query.Trim().Length >= MinimumQueryLength;

    /// <summary>
    /// Updates dialog context when the owning component receives new parameters.
    /// Pending and accepted results are invalidated when their originating group snapshot or culture changes.
    /// </summary>
    public void UpdateContext(MarkdownDocumentGroup? group, string? culture)
    {
        if (IsDisposeRequested)
        {
            return;
        }

        CancellationTokenSource? previous;
        lock (_searchLock)
        {
            if (IsDisposeRequested || (ReferenceEquals(CurrentGroup, group)
                && string.Equals(CurrentCulture, culture, StringComparison.OrdinalIgnoreCase))) return;
            previous = _activeSearchCancellation;
            _activeSearchCancellation = null;
            CurrentGroup = group;
            CurrentCulture = culture;
            Results = [];
            ErrorMessage = null;
            IsSearching = false;
        }
        CancelSearch(previous);
    }

    /// <summary>
    /// Updates the query from the input field.
    /// </summary>
    public void OnQueryChanged(string? value)
    {
        if (IsDisposeRequested)
        {
            return;
        }

        Query = value ?? string.Empty;
        CancelActiveSearch();
        IsSearching = false;

        if (HasEnoughQueryLength)
        {
            return;
        }

        ErrorMessage = null;
        Results = [];
        IsSearching = false;
    }

    /// <summary>
    /// Executes a debounced search when the input settles.
    /// </summary>
    public Task OnQueryDebouncedAsync(string value)
    {
        if (IsDisposeRequested)
        {
            return Task.CompletedTask;
        }

        Query = value ?? string.Empty;
        return StartSearch();
    }

    /// <summary>
    /// Toggles whether all knowledge bases participate in the search.
    /// </summary>
    public Task OnIncludeAllKnowledgeBasesChangedAsync(bool value)
    {
        if (IsDisposeRequested)
        {
            return Task.CompletedTask;
        }

        IncludeAllKnowledgeBases = value;
        return StartSearch();
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
            Task[] searchOperations;
            lock (_searchLock)
            {
                IsSearching = false;
                searchOperations = _searchOperations.ToArray();
            }

            var cancellation = _lifetimeCancellation.CancelAsync();
            await Task.WhenAll(searchOperations.Append(cancellation));
            _disposedCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposedCompletion.TrySetException(exception);
            throw;
        }
        finally { _lifetimeCancellation.Dispose(); }
    }

    private Task StartSearch()
    {
        CancellationTokenSource? previous;
        Task searchTask;
        lock (_searchLock)
        {
            if (IsDisposeRequested)
            {
                return Task.CompletedTask;
            }

            previous = _activeSearchCancellation;
            _activeSearchCancellation = null;
            ErrorMessage = null;

            if (!HasEnoughQueryLength)
            {
                Results = [];
                IsSearching = false;
                searchTask = Task.CompletedTask;
            }
            else
            {
                var searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
                var request = new MarkdownDocumentSearchRequest(
                    Query.Trim(), CurrentGroup?.Key, IncludeAllKnowledgeBases, CurrentCulture);
                _activeSearchCancellation = searchCancellation;
                IsSearching = true;
                _searchOperations.RemoveAll(static task => task.IsCompleted);
                searchTask = ExecuteSearchAsync(request, searchCancellation);
                _searchOperations.Add(searchTask);
            }
        }
        CancelSearch(previous);
        return searchTask;
    }

    private async Task ExecuteSearchAsync(
        MarkdownDocumentSearchRequest request,
        CancellationTokenSource searchCancellation)
    {
        // Let StartSearch publish ownership before a synchronously completed provider can finish.
        await Task.Yield();

        try
        {
            var response = await markdownFacade.SearchDocumentsAsync(
                request,
                searchCancellation.Token);

            lock (_searchLock)
            {
                if (!IsCurrentSearch(searchCancellation))
                {
                    return;
                }

                if (response.IsFailure())
                {
                    ErrorMessage = response.Message ?? string.Empty;
                    Results = [];
                    return;
                }

                Results = response.Data ?? throw new InvalidOperationException("The document search returned no data.");
            }
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_searchLock)
            {
                if (IsCurrentSearch(searchCancellation))
                {
                    ErrorMessage = ex.Message;
                    Results = [];
                }
            }
        }
        finally
        {
            lock (_searchLock)
            {
                if (ReferenceEquals(_activeSearchCancellation, searchCancellation))
                {
                    if (!IsDisposeRequested)
                    {
                        IsSearching = false;
                    }

                    _activeSearchCancellation = null;
                }
            }

            searchCancellation.Dispose();
        }
    }

    private void CancelActiveSearch()
    {
        CancellationTokenSource? cancellation;
        lock (_searchLock) cancellation = _activeSearchCancellation;
        CancelSearch(cancellation);
    }

    private static void CancelSearch(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private bool IsCurrentSearch(CancellationTokenSource searchCancellation)
    {
        return !IsDisposeRequested
               && !searchCancellation.IsCancellationRequested
               && ReferenceEquals(_activeSearchCancellation, searchCancellation);
    }

    private bool IsDisposeRequested => Volatile.Read(ref _disposeRequested) != 0;
}
