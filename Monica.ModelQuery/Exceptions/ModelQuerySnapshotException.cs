namespace Monica.ModelQuery.Exceptions;

/// <summary>
/// Exception thrown when snapshot construction or configuration fails.
/// </summary>
public class ModelQuerySnapshotException(string displayMessage, string? technicalDetail = null)
    : ModelQueryException(displayMessage, technicalDetail);

/// <summary>
/// Exception thrown when a snapshot encounters an unsupported field type.
/// </summary>
public class ModelQueryUnsupportedFieldTypeException(
    string displayMessage,
    string technicalDetail,
    Type fromPropertyType)
    : ModelQuerySnapshotException(displayMessage, technicalDetail)
{
    /// <summary>
    /// Unsupported field type involved in the error.
    /// </summary>
    public Type FromPropertyType { get; } = fromPropertyType;
}
