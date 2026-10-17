namespace Monica.Application.Annotations;

/// <summary>
/// Identifies an HTTP method supported by request-owned endpoint generation.
/// </summary>
public enum ApiHttpMethod
{
    /// <summary>Retrieves a resource.</summary>
    Get,

    /// <summary>Creates or invokes a resource operation.</summary>
    Post,

    /// <summary>Replaces a resource.</summary>
    Put,

    /// <summary>Partially updates a resource.</summary>
    Patch,

    /// <summary>Deletes a resource.</summary>
    Delete
}
