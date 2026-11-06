// ReSharper disable once CheckNamespace
namespace Monica.Core.Results;

/// <summary>
/// Defines the transport outcome carried by a Monica result envelope. This enum is the closed whitelist of
/// supported outcomes: every defined value maps one-to-one to its HTTP status, values outside this enum are
/// contract defects at API boundaries, and business semantics belong to the <c>metadata.error</c> reason codes
/// (<see cref="ResultErrorCodes"/>) rather than to these numbers.
/// </summary>
public enum ResultStatus
{
    /// <summary>
    /// No result status has been assigned.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The request completed successfully.
    /// </summary>
    Ok = 200,

    /// <summary>
    /// The request completed and created a resource.
    /// </summary>
    Created = 201,

    /// <summary>
    /// The request is invalid.
    /// </summary>
    BadRequest = 400,

    /// <summary>
    /// Authentication is required.
    /// </summary>
    Unauthorized = 401,

    /// <summary>
    /// The authenticated caller is not allowed to perform the operation.
    /// </summary>
    Forbidden = 403,

    /// <summary>
    /// The requested resource does not exist.
    /// </summary>
    NotFound = 404,

    /// <summary>
    /// The request conflicts with the current state of the target resource.
    /// </summary>
    Conflict = 409,

    /// <summary>
    /// The request payload exceeds the server's accepted size.
    /// </summary>
    PayloadTooLarge = 413,

    /// <summary>
    /// The request content type is not supported by the target operation.
    /// </summary>
    UnsupportedMediaType = 415,

    /// <summary>The responding service is limiting the request rate.</summary>
    TooManyRequests = 429,

    /// <summary>
    /// An unexpected server error occurred.
    /// </summary>
    InternalError = 500,

    /// <summary>A dependency returned an invalid or unsuccessful non-contract response.</summary>
    BadGateway = 502,

    /// <summary>A required dependency is unavailable.</summary>
    ServiceUnavailable = 503,

    /// <summary>A required dependency did not complete within the call deadline.</summary>
    GatewayTimeout = 504,
}
