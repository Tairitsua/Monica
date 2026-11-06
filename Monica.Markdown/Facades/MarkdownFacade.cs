using Microsoft.Extensions.Logging;
using Monica.Core.Results;
using Monica.Markdown.Abstractions;
using Monica.Markdown.Models;
using Monica.Markdown.UIMarkdown.Models;
using Monica.Tool.Algorithms.Trees;

namespace Monica.Markdown.Facades;

/// <summary>
/// Host-facing markdown entry point for UI components and other presentation adapters.
/// Converts internal exceptions into the unified <see cref="Result"/> result model.
/// </summary>
public class MarkdownFacade(
    IMarkdownDocumentCatalog markdownCatalog,
    IMarkdownDocumentSearcher markdownSearcher,
    ILogger<MarkdownFacade> logger)
{
    /// <summary>
    /// Gets all registered document groups.
    /// </summary>
    public async Task<Result<List<MarkdownDocumentGroup>>> GetAllDocumentGroupsAsync()
    {
        try
        {
            var groups = await markdownCatalog.GetAllDocumentGroupsAsync();
            return Result.Success(groups);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load document groups.");
            return Result.Failure<List<MarkdownDocumentGroup>>($"Failed to load document groups: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets a single document group by key.
    /// </summary>
    public async Task<Result<MarkdownDocumentGroup>> GetDocumentGroupAsync(string groupKey)
    {
        try
        {
            var group = await markdownCatalog.GetDocumentGroupAsync(groupKey);
            return Result.Success(group);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load document group '{GroupKey}'.", groupKey);
            return Result.Failure<MarkdownDocumentGroup>($"Failed to load document group: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the hierarchical tree structure for a document group.
    /// For multilingual groups, providing a culture returns the visible tree
    /// rooted at that language folder.
    /// </summary>
    public async Task<Result<TreeNode<MarkdownDocumentNodeData>>> GetDocumentTreeAsync(
        string groupKey,
        string? culture = null)
    {
        try
        {
            var tree = await markdownCatalog.GetDocumentTreeAsync(groupKey, culture);
            return Result.Success(tree);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load document tree for group '{GroupKey}'.", groupKey);
            return Result.Failure<TreeNode<MarkdownDocumentNodeData>>($"Failed to load document tree: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the raw markdown content of a document.
    /// </summary>
    public async Task<Result<string>> GetDocumentContentAsync(MarkdownDocument document)
    {
        try
        {
            var content = await markdownCatalog.GetDocumentContentAsync(document);
            return Result.Success<string>(content);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load markdown document content from '{Path}'.", document.FilePath);
            return Result.Failure<string>($"Failed to load document content: {ex.Message}");
        }
    }

    /// <summary>
    /// Searches markdown documents and returns UI-friendly result descriptors.
    /// </summary>
    public async Task<Result<IReadOnlyList<MarkdownDocumentSearchResult>>> SearchDocumentsAsync(
        MarkdownDocumentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await markdownSearcher.SearchAsync(request, cancellationToken);
            return Result.Success<IReadOnlyList<MarkdownDocumentSearchResult>>(results);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search markdown documents for query '{Query}'.", request.Query);
            return Result.Failure<IReadOnlyList<MarkdownDocumentSearchResult>>($"Failed to search markdown documents: {ex.Message}");
        }
    }

    /// <summary>
    /// Rescans a specific document group.
    /// </summary>
    public async Task<Result> RefreshGroupAsync(string groupKey)
    {
        try
        {
            await markdownCatalog.RefreshAsync(groupKey);
            return Result.Success().WithMessage("Group refreshed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh markdown group '{GroupKey}'.", groupKey);
            return Result.Failure($"Failed to refresh group: {ex.Message}");
        }
    }

    /// <summary>
    /// Rescans all registered document groups.
    /// </summary>
    public async Task<Result> RefreshAllAsync()
    {
        try
        {
            await markdownCatalog.RefreshAllAsync();
            return Result.Success().WithMessage("All groups refreshed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh all markdown groups.");
            return Result.Failure($"Failed to refresh all groups: {ex.Message}");
        }
    }
}
