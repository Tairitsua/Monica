namespace Monica.ModelQuery.Exceptions;

/// <summary>
/// Exception thrown when a field value cannot be converted.
/// </summary>
public class ModelQueryValueConversionException(string displayMessage, string? technicalDetail = null)
    : ModelQueryException(displayMessage, technicalDetail);
