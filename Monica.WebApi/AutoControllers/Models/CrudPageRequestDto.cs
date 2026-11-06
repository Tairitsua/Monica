using Monica.ModelQuery.Abstractions;
using Monica.WebApi.AutoControllers.Abstractions;

namespace Monica.WebApi.AutoControllers.Models;

/// <summary>
/// Default paged CRUD request DTO.
/// </summary>
public class CrudPageRequestDto : LimitedResultRequestDto, IHasRequestFilter, IHasRequestSelect, IHasRequestPage, IHasRequestFeature, IHasRequestSorting, IHasRequestKeysetPage
{
    /// <inheritdoc />
    public int? Page { get; set; }

    /// <inheritdoc />
    public bool? DisablePage { get; set; }

    /// <inheritdoc />
    public string? Filter { get; set; }

    /// <inheritdoc />
    public string? Fuzzy { get; set; }

    /// <inheritdoc />
    public string? FuzzyColumns { get; set; }

    /// <inheritdoc />
    public string? SelectColumns { get; set; }

    /// <inheritdoc />
    public string? SelectExceptColumns { get; set; }

    /// <inheritdoc />
    public string? Features { get; set; }

    /// <inheritdoc />
    public int? SkipCount { get; set; }

    /// <inheritdoc />
    public string? Sorting { get; set; }

    /// <inheritdoc />
    public string? Cursor { get; set; }

    /// <inheritdoc />
    protected override int GetMaximumResultCount(AutoControllerPaginationOption pagination)
    {
        return pagination.MaximumCrudResultCount;
    }
}
