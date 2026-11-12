using Monica.Core.ExceptionHandling.Exceptions;

namespace Monica.ModelQuery.Exceptions;

/// <summary>
/// Base exception for ModelQuery execution failures.
/// </summary>
public class ModelQueryException : DisplayMessageException
{
    /// <summary>
    /// Creates a new ModelQuery exception.
    /// </summary>
    /// <param name="displayMessage">User-facing message displayed to clients.</param>
    /// <param name="technicalDetail">Optional technical detail for diagnostics.</param>
    public ModelQueryException(string displayMessage, string? technicalDetail = null)
        : base(displayMessage, technicalDetail)
    {
    }
}
