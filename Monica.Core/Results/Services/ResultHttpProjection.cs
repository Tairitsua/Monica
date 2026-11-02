using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Results.Abstractions;

namespace Monica.Core.Results.Services;

/// <summary>Projects a declared Monica result into HTTP without owning remote networking.</summary>
public static class ResultHttpProjection
{
    /// <summary>Creates a Minimal API JSON result with the result's mapped HTTP status.</summary>
    public static IResult ToMinimalApiResult(IResultEnvelope response) => new EnvelopeHttpResult(response);

    /// <summary>Creates an MVC result with one authoritative HTTP status.</summary>
    public static ObjectResult ToMvcResult(IResultEnvelope response) =>
        new EnvelopeObjectResult(response) { StatusCode = GetStatusCode(response) };

    /// <summary>Normalizes a result with the current host's canonical serialization and message policy.</summary>
    public static void Prepare(IResultEnvelope response, HttpContext context) =>
        response.PrepareForPresentation(
            context.RequestServices.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions,
            context.RequestServices.GetRequiredService<IResultErrorMessageProvider>(), ResultTraceId.Capture(context),
            exposeReservedDiagnostics: context.RequestServices
                .GetRequiredService<IMonicaApplicationOptions>().DebugMode);

    /// <summary>Resolves the transport status; unassigned or unsupported statuses are local contract errors.</summary>
    public static int GetStatusCode(IResultEnvelope response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return (int)(response.ToHttpStatusCode()
                     ?? throw new InvalidOperationException("An API result must declare its status before it is written."));
    }

    private sealed class EnvelopeHttpResult(IResultEnvelope response) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            Prepare(response, httpContext);
            httpContext.Response.StatusCode = GetStatusCode(response);
            return httpContext.Response.WriteAsJsonAsync(response, response.GetType(),
                httpContext.RequestServices.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions,
                cancellationToken: httpContext.RequestAborted);
        }
    }

    private sealed class EnvelopeObjectResult(IResultEnvelope response) : ObjectResult(response)
    {
        public override Task ExecuteResultAsync(ActionContext context)
        {
            Prepare(response, context.HttpContext);
            StatusCode = GetStatusCode(response);
            return base.ExecuteResultAsync(context);
        }
    }
}
