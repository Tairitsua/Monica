using System.Net;
using Monica.Core.Results;

namespace Monica.Invocation.Models;

/// <summary>Describes a classified dependency failure; transport details are for diagnostics only.</summary>
/// <param name="Status">Status for the caller-facing result.</param>
/// <param name="Code">Stable public failure reason.</param>
/// <param name="Stage">Safe diagnostic stage.</param>
/// <param name="UpstreamStatus">Actual HTTP status when received.</param>
/// <param name="RetryAfter">Validated Retry-After value, retained only in diagnostics.</param>
public sealed record RemoteCallFailure(ResStatus Status, string Code, string Stage,
    HttpStatusCode? UpstreamStatus = null, string? RetryAfter = null)
{
    /// <summary>Classifies an HTTP response that did not contain a valid application envelope.</summary>
    public static RemoteCallFailure FromHttp(HttpStatusCode status, string stage, string? retryAfter = null) =>
        (int)status switch
        {
            408 or 504 => new(ResStatus.GatewayTimeout, ResultErrorCodes.DependencyTimeout, stage, status),
            429 => new(ResStatus.ServiceUnavailable, ResultErrorCodes.DependencyRateLimited, stage, status, retryAfter),
            503 => new(ResStatus.ServiceUnavailable, ResultErrorCodes.DependencyUnavailable, stage, status, retryAfter),
            >= 400 and < 500 => new(ResStatus.BadGateway, ResultErrorCodes.DependencyRejected, stage, status),
            >= 500 => new(ResStatus.BadGateway, ResultErrorCodes.DependencyFailed, stage, status),
            _ => InvalidResponse(status, stage)
        };

    /// <summary>Classifies a response that violated the declared wire contract.</summary>
    public static RemoteCallFailure InvalidResponse(HttpStatusCode? status, string stage) =>
        new(ResStatus.BadGateway, ResultErrorCodes.DependencyInvalidResponse, stage, status);
}
