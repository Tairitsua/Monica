namespace Monica.Core.Results;

/// <summary>Contains the items and optional navigation information for one page of query results.</summary>
/// <typeparam name="T">The page item type.</typeparam>
/// <param name="totalCount">The total number of matching items before paging, or null when unknown.</param>
/// <param name="items">The items on this page. Supply an empty collection when the page has no items.</param>
/// <param name="currentPage">The one-based page number, or null by default.</param>
/// <param name="pageSize">The requested page size, or null by default.</param>
/// <param name="cursor">The continuation cursor, or null by default.</param>
public sealed class Page<T>(int? totalCount, IReadOnlyList<T> items,
    int? currentPage = null, int? pageSize = null, string? cursor = null)
{
    /// <summary>Gets the total number of matching items before paging.</summary>
    public int? TotalCount { get; } = totalCount;

    /// <summary>Gets the items on this page, which may be empty.</summary>
    public IReadOnlyList<T> Items { get; } = items;

    /// <summary>Gets the requested page size when supplied.</summary>
    public int? PageSize { get; } = pageSize;

    /// <summary>Gets the one-based page number when supplied.</summary>
    public int? CurrentPage { get; } = currentPage;

    /// <summary>Gets the total number of pages when the count and a positive page size are available.</summary>
    public int? TotalPages => TotalCount is { } count && PageSize is > 0
        ? (int)Math.Ceiling((double)count / PageSize.Value)
        : null;

    /// <summary>Gets whether the supplied page number has an earlier page, or null when it is unknown.</summary>
    public bool? HasPrevious => CurrentPage is { } page ? page > 1 : null;

    /// <summary>Gets whether the supplied page number is below the total page count, or null when the page number is unknown.</summary>
    public bool? HasNext => CurrentPage is { } page ? page < TotalPages : null;

    /// <summary>Gets the continuation cursor when supplied.</summary>
    public string? Cursor { get; } = cursor;
}
