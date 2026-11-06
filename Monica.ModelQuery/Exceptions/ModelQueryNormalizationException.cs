namespace Monica.ModelQuery.Exceptions;

/// <summary>
/// Exception thrown when an expression or query cannot be normalized or parsed.
/// </summary>
public class ModelQueryNormalizationException(string displayMessage, string? technicalDetail = null)
    : ModelQueryException(displayMessage, technicalDetail);

/// <summary>
/// Exception thrown when token expression generation fails.
/// </summary>
public class ModelQueryExpressionGenerationException(string displayMessage, string? technicalDetail = null)
    : ModelQueryException(displayMessage, technicalDetail);
