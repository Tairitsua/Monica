namespace Monica.ModelQuery.Exceptions;

/// <summary>
/// Exception thrown when an ModelQuery invocation fails.
/// </summary>
public class ModelQueryExecutionException(string displayMessage, string? technicalDetail = null)
    : ModelQueryException(displayMessage, technicalDetail);
