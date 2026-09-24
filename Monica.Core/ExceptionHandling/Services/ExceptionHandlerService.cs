using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.ExceptionHandling.Exceptions;
using Monica.Core.ExceptionHandling.Models;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Modules;

namespace Monica.Core.ExceptionHandling.Services;

internal class ExceptionHandlerService(
    ILogger<ExceptionHandlerService> logger,
    IHttpContextAccessor accessor,
    IEnumerable<IExceptionResponseMapper> mappers,
    IOptions<ModuleResultEnvelopeOption> envelopeOptions,
    IJsonSerializerOptionsProvider serializer,
    IResultErrorMessageProvider messages,
    IExceptionResponseDiagnostics? diagnostics = null,
    IEnumerable<IRemoteExceptionDiagnosticsExtractor>? remoteExtractors = null) : IExceptionHandlerService
{
    public Task<Res> HandleCurrentHttpContextAsync(Exception exception, CancellationToken cancellationToken) =>
        HandleAsync(accessor.HttpContext, exception, cancellationToken);

    public void LogException(HttpContext? httpContext, Exception exception, Res response)
    {
        var status = (int)(response.ToHttpStatusCode() ?? System.Net.HttpStatusCode.InternalServerError);
        response.TryGetError(serializer.SerializerOptions, out var error);
        var level = status >= 500 ? LogLevel.Error : LogLevel.Information;
        // Operator logs always keep the full exception object; the host's diagnostic switch only affects the envelope.
        logger.Log(level, exception,
            "Request failed: {ErrorCode}; HTTP {StatusCode}; exception {ExceptionType}; trace {TraceId}",
            error?.Code, status, exception.GetType().Name, error?.TraceId);
    }

    public Task<Res> HandleAsync(HttpContext? httpContext, Exception exception, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exposeDiagnostics = envelopeOptions.Value.ExposeDiagnosticDetails;
        var metadata = new List<KeyValuePair<string, object?>>();
        while (exception is ContextualException contextual)
        {
            metadata.AddRange(contextual.Metadata);
            if (contextual.InnerException is null) break;
            exception = contextual.InnerException;
        }

        Res? result = null;
        foreach (var mapper in mappers)
        {
            if (mapper.TryMap(httpContext, exception, cancellationToken, out result)) break;
        }
        result ??= exception switch
        {
            BusinessException business => Res.Fail(business.Message),
            DisplayMessageException display => new Res(display.DisplayMessage, display.ResultStatus)
                .WithDetail(display.TechnicalDetail),
            _ => Res.Fail("", ResStatus.InternalError)
        };
        // All mappers pass through the same reserved-metadata policy. Outer context cannot replace an error.
        foreach (var entry in metadata.Where(entry => entry.Key != "error"))
            result.SetMetadata(entry.Key, entry.Value);
        result.PrepareForPresentation(serializer.SerializerOptions, messages, ResultTraceId.Capture(httpContext),
            exposeReservedDiagnostics: exposeDiagnostics);
        if (exposeDiagnostics)
        {
            var document = ExceptionDiagnosticProjection.GetOrCreate(result);
            document.ExceptionId = ExceptionDiagnosticProjection.Capture(document, exception, remoteExtractors);
            document.Request = new DiagnosticRequest(httpContext?.Request.Method, httpContext?.Request.Path,
                httpContext?.GetEndpoint()?.DisplayName, DateTime.UtcNow);
        }
        // Exception-path diagnostics (call-chain correlation) run after presentation so the reserved-member
        // policy has already been applied.
        diagnostics?.Attach(httpContext, result);
        return Task.FromResult(result);
    }

}
