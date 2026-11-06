using Monica.ModelQuery.Exceptions;
using Monica.ModelQuery.Models;

namespace Monica.ModelQuery.Abstractions;

/// <summary>
/// Converts raw filter values to typed ModelQuery values.
/// </summary>
internal interface IModelQueryValueConverter
{
    /// <summary>
    /// Converts a raw field value.
    /// </summary>
    /// <param name="value">The raw string value.</param>
    /// <param name="typeSetting">The target field type metadata.</param>
    /// <param name="features">Additional condition features that affect conversion.</param>
    /// <exception cref="ModelQueryValueConversionException">Thrown when the value cannot be converted.</exception>
    /// <returns>The converted value.</returns>
    dynamic? ConvertEntrance(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features);
}
