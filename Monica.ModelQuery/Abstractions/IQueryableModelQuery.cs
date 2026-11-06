using System.Linq.Expressions;
using Monica.ModelQuery.Models;

namespace Monica.ModelQuery.Abstractions;

/// <summary>
/// Provides ModelQuery operations for <see cref="IQueryable{T}"/> sources.
/// </summary>
/// <typeparam name="TModel">The model type.</typeparam>
public interface IQueryableModelQuery<TModel> : IModelQuery<TModel>
{
    /// <summary>
    /// Applies a filter expression.
    /// </summary>
    /// <param name="queryable">The source query.</param>
    /// <param name="filter">The filter expression.</param>
    /// <returns>The filtered query.</returns>
    IQueryable<TModel> ApplyFilter(IQueryable<TModel> queryable, string filter);
    

    /// <summary>
    /// Applies a normalized filter.
    /// </summary>
    /// <param name="queryable">The source query.</param>
    /// <param name="result">The normalized filter result.</param>
    /// <returns>The filtered query.</returns>
    IQueryable<TModel> ApplyFilter(IQueryable<TModel> queryable, NormalizedFilter result);

    /// <summary>
    /// Applies a filter for a single field.
    /// </summary>
    /// <param name="queryable">The source query.</param>
    /// <param name="selector">The field selector.</param>
    /// <param name="condition">The field condition.</param>
    /// <param name="value">The raw field value.</param>
    /// <returns>The filtered query.</returns>
    IQueryable<TModel> ApplyFilter(IQueryable<TModel> queryable, Expression<Func<TModel, object>> selector,
        EFieldConditions condition, string value);
    /// <summary>
    /// Applies a fuzzy-match filter.
    /// </summary>
    /// <param name="queryable">The source query.</param>
    /// <param name="fuzzy">The fuzzy-search value.</param>
    /// <param name="fuzzyColumns">Optional fields to include in fuzzy searching.</param>
    /// <returns>The filtered query.</returns>
    IQueryable<TModel> ApplyFuzzy(IQueryable<TModel> queryable, string fuzzy, string? fuzzyColumns = null);

    /// <summary>
    /// Selects the specified fields.
    /// </summary>
    /// <param name="queryable">The source query.</param>
    /// <param name="selectColumns">The selected fields.</param>
    /// <returns>A dynamically projected query.</returns>
    IQueryable SelectFields(IQueryable<TModel> queryable, string selectColumns);
    /// <summary>
    /// Selects all fields except the specified ones.
    /// </summary>
    /// <param name="queryable">The source query.</param>
    /// <param name="selectExceptColumns">The fields to exclude.</param>
    /// <returns>A dynamically projected query.</returns>
    IQueryable SelectFieldsExcept(IQueryable<TModel> queryable, string selectExceptColumns);
}
