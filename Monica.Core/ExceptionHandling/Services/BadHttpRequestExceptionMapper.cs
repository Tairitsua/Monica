using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.Results;

namespace Monica.Core.ExceptionHandling.Services;

/// <summary>
/// Converts ASP.NET Core request-binding failures into the standard Monica response envelope.
/// </summary>
internal sealed class BadHttpRequestExceptionMapper(IRequestRejectionFactory rejections) : IExceptionResponseMapper
{
    public bool TryMap(
        HttpContext? httpContext,
        Exception exception,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out Result? response)
    {
        if (exception is not BadHttpRequestException badRequestException)
        {
            response = null;
            return false;
        }

        response = rejections.FromBadRequest(httpContext, badRequestException).ToResult();
        return true;
    }
}
