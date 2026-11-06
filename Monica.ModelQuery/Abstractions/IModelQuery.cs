using Monica.ModelQuery.Models;

namespace Monica.ModelQuery.Abstractions;

/// <summary>
/// Common ModelQuery field-resolution and normalization operations.
/// </summary>
public interface IModelQuery<TModel>
{
    /// <summary>
    /// Resolves the specified query names to ModelQuery fields.
    /// </summary>
    /// <param name="selectProperties">The query names to resolve.</param>
    /// <returns>The matching ModelQuery field objects.</returns>
    List<QueryField> GetFields(params string[] selectProperties);
    /// <summary>
    /// Gets all ModelQuery fields except the fields resolved by the specified query names.
    /// </summary>
    /// <param name="selectProperties">The query names whose fields should be excluded.</param>
    /// <returns>The remaining ModelQuery field objects.</returns>
    List<QueryField> GetFieldsExcept(params string[] selectProperties);

    /// <summary>
    /// Resolves a field-selection expression to ModelQuery fields.
    /// </summary>
    /// <param name="selectExpression">The field-selection expression separated by <see cref="ModelQueryExpressionOptions.SelectSeparator"/>.</param>
    /// <param name="isReverseSelect">Whether this is a reverse selection that excludes the specified fields.</param>
    /// <returns>The resolved ModelQuery fields.</returns>
    List<QueryField> NormalizeLiteralSelect(string selectExpression, bool isReverseSelect = false);
    /// <summary>
    /// <inheritdoc cref="NormalizeLiteralSelect"/>
    /// </summary>
    /// <param name="selectExpression">The field-selection expression separated by <see cref="ModelQueryExpressionOptions.SelectSeparator"/>.</param>
    /// <param name="isReverseSelect">Whether this is a reverse selection that excludes the specified fields.</param>
    /// <returns>A tuple containing the resolved fields and the field names that could not be resolved.</returns>
    (List<QueryField> fields, List<string> failedList) NormalizeLiteralSelectWithoutException(string selectExpression,
        bool isReverseSelect = false);

    /// <summary>
    /// Gets the normalized result of a filter expression.
    /// </summary>
    /// <param name="filter">The filter expression.</param>
    /// <returns>The normalized filter result.</returns>
    NormalizedFilter NormalizeFilter(string filter);
}
