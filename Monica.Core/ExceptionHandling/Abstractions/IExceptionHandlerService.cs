using Microsoft.AspNetCore.Http;
using Monica.Core.Results;

namespace Monica.Core.ExceptionHandling.Abstractions;

public interface IExceptionHandlerService
{
    Task<Result> HandleAsync(HttpContext? httpContext, Exception exception, CancellationToken cancellationToken);
    Task<Result> HandleCurrentHttpContextAsync(Exception exception, CancellationToken cancellationToken);

    /// <summary>
    /// Logs a handled exception at a severity derived from its mapped response.
    /// Client failures are logged without an error-level stack trace, while server failures remain errors.
    /// </summary>
    void LogException(HttpContext? httpContext, Exception exception, Result response);
}
