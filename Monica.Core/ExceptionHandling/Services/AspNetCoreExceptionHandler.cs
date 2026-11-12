using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.Results;

namespace Monica.Core.ExceptionHandling.Services;

/// <summary>
/// ASP.NET Core exception handler that converts unhandled exceptions into Monica responses.
/// </summary>
public class AspNetCoreExceptionHandler(IExceptionHandlerService handler) : IExceptionHandler
{
    public virtual async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var res = await handler.HandleAsync(httpContext, exception, cancellationToken);
        handler.LogException(httpContext, exception, res);
        httpContext.Response.StatusCode =
            (int)(res.ToHttpStatusCode() ?? HttpStatusCode.InternalServerError);

        return await WriteResponseAsync(httpContext, res, exception, cancellationToken);
    }

    protected async ValueTask<bool> WriteResponseAsync(
        HttpContext httpContext,
        Result response,
        Exception originalException,
        CancellationToken cancellationToken)
    {
        var serializerOptions = httpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        try
        {
            var payload = JsonSerializer.Serialize(response, serializerOptions);
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await httpContext.Response.WriteAsync(payload, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception writeException)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILogger<AspNetCoreExceptionHandler>>();
            logger.LogError(
                "Failed to serialize an exception response: {ExceptionType}; original {OriginalExceptionType}; trace {TraceId}",
                writeException.GetType().Name, originalException.GetType().Name, ResultTraceId.Capture(httpContext));

            if (httpContext.Response.HasStarted)
            {
                return false;
            }

            httpContext.Response.Clear();
            httpContext.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            httpContext.Response.ContentType = "application/json; charset=utf-8";

            var fallbackResponse = Result.Failure(
                "An unexpected server error occurred.",
                ResultStatus.InternalError).SetError(new ResultError(ResultErrorCodes.UnexpectedError, ResultTraceId.Capture(httpContext)));

            var fallback = JsonSerializer.Serialize(fallbackResponse, serializerOptions);

            await httpContext.Response.WriteAsync(fallback, cancellationToken);
            return true;
        }
    }
}
