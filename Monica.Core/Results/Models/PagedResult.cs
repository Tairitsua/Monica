using System.Dynamic;
using System.Text.Json.Serialization;
using Monica.Core.Results.Abstractions;
using Monica.Tool.Extensions;

namespace Monica.Core.Results;

/// <summary>Creates pagination result envelopes.</summary>
public static class PagedResult
{
    /// <summary>Creates a successful page with the supplied items and optional pagination state.</summary>
    /// <remarks>Optional page number, size, and cursor default to null. Navigation values follow the supplied count and size.</remarks>
    public static PagedResult<T> Success<T>(int totalCount, IReadOnlyList<T> items,
        int? currentPage = null, int? pageSize = null, string? cursor = null) => new()
    {
        Status = ResultStatus.Ok,
        Data = new PagedResult<T>.PageData(totalCount, items)
        {
            CurrentPage = currentPage,
            PageSize = pageSize,
            Cursor = cursor
        }
    };

    /// <summary>Creates a failure containing an empty page.</summary>
    /// <param name="message">A safe message suitable for display to the caller.</param>
    /// <param name="status">The failure status, defaulting to <see cref="ResultStatus.BadRequest"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The status is not a defined failure status.</exception>
    public static PagedResult<T> Failure<T>(string message, ResultStatus status = ResultStatus.BadRequest)
    {
        Result.ValidateFailureStatus(status);
        return new PagedResult<T> { Message = message, Status = status, Data = new PagedResult<T>.PageData(0, []) };
    }

    /// <summary>Copies a failure into a paged envelope with an empty page.</summary>
    /// <remarks>The metadata dictionary is copied; its values are shared.</remarks>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="InvalidOperationException">The source is successful or has an invalid status.</exception>
    public static PagedResult<T> FromFailure<T>(IResultEnvelope source)
    {
        Result.ValidateFailureSource(source);
        return new PagedResult<T>
        {
            Message = source.Message,
            Status = source.Status,
            Metadata = Result.CopyMetadata(source.Metadata),
            Data = new PagedResult<T>.PageData(0, [])
        };
    }
}

/// <summary>A mutable result envelope containing a page of items.</summary>
/// <typeparam name="T">The type of each page item.</typeparam>
/// <remarks>The parameterless constructor leaves status unknown and page sum and items null for serializers.</remarks>
public class PagedResult<T> : IRemoteResultEnvelope<PagedResult<T>>
{
    /// <inheritdoc />
    public string? Message { get; set; }

    /// <inheritdoc />
    public ResultStatus Status { get; set; } = ResultStatus.Unknown;

    /// <inheritdoc />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExpandoObject? Metadata { get; set; }

    /// <summary>Gets or sets the page state and items.</summary>
    public PageData Data { get; set; } = new(null, null);

    /// <summary>Creates an envelope with an unknown status for serializers and adapters.</summary>
    public PagedResult() { }

    /// <inheritdoc />
    public static PagedResult<T> CreateRemoteFailure(ResultStatus status, string message)
    {
        Result.ValidateFailureStatus(status);
        return new PagedResult<T> { Status = status, Message = message };
    }

    /// <summary>Creates an untyped envelope, intentionally discarding the page.</summary>
    /// <remarks>Status and message are preserved; the metadata dictionary is copied and its values are shared.</remarks>
    public Result WithoutData() => new()
    {
        Message = Message,
        Status = Status,
        Metadata = Result.CopyMetadata(Metadata)
    };

    /// <summary>Page item and navigation data.</summary>
    /// <param name="sum">Total item count, when known.</param>
    /// <param name="items">Items in this page, when available.</param>
    public class PageData(int? sum, IReadOnlyList<T>? items)
    {
        /// <summary>Gets or sets the total number of items.</summary>
        [JsonPropertyName("sum")]
        public int? Sum { get; set; } = sum;

        /// <summary>Gets or sets the items in the current page.</summary>
        [JsonPropertyName("items")]
        public IReadOnlyList<T>? Items { get; set; } = items;

        /// <summary>Gets or sets the requested page size.</summary>
        [JsonPropertyName("pageSize")]
        public int? PageSize { get; set; }

        /// <summary>Gets or sets the current page number.</summary>
        [JsonPropertyName("currentPage")]
        public int? CurrentPage { get; set; }

        /// <summary>Gets the total number of pages when count and page size are available.</summary>
        [JsonPropertyName("totalPages")]
        public int? TotalPages => ((double?)Sum / PageSize)?.Ceiling();

        /// <summary>Gets whether an earlier page is available when the current page is known.</summary>
        [JsonPropertyName("hasPrevious")]
        public bool? HasPrevious => CurrentPage == null ? null : CurrentPage > 1;

        /// <summary>Gets whether a later page is available when the current page is known.</summary>
        [JsonPropertyName("hasNext")]
        public bool? HasNext => CurrentPage == null ? null : CurrentPage < TotalPages;

        /// <summary>Gets or sets a continuation cursor.</summary>
        [JsonPropertyName("cursor")]
        public string? Cursor { get; set; }
    }
}
