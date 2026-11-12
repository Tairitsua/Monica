using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Framework.ChainTracing.Extensions;
using Monica.Framework.ChainTracing.Models;
using Monica.Invocation.Models;

namespace Monica.Framework.ChainTracing.Providers.Actor;

/// <summary>
/// Traces actor calls without buffering or enriching their public response bodies. The node reflects the
/// observed outcome: a propagated exception, or the HTTP status the actor runtime produced — Dapr maps
/// actor failures to error statuses, while a 200 body's envelope semantics are already recorded by the
/// business-execution nodes underneath.
/// </summary>
internal sealed class ActorInvocationTracingMiddleware(
    IJsonSerializerOptionsProvider jsonSerializerOptionsProvider,
    IChainTracing tracing) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.GetEndpoint()?.DisplayName != "Dapr Actors Invoke")
        {
            await next(context);
            return;
        }

        context.Request.EnableBuffering();

        if (context.Request.Body.CanRead)
        {
            try
            {
                context.Request.Body.Position = 0;

                if (JsonSerializer.Deserialize<ApiRequest>(context.Request.Body, jsonSerializerOptionsProvider.SerializerOptions) is { Headers.Count: > 0 } request)
                {
                    foreach (var (key, value) in request.Headers.Where(p => p.Key.StartsWith("X-")))
                    {
                        if (!context.Request.Headers.ContainsKey(key))
                        {
                            context.Request.Headers.Append(key, value);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                context.Request.Body.Position = 0;
            }
        }

        using var scope = tracing.BeginScope("Actor invocation", "Actor");
        // Publish the chain for the exception handler, which cannot observe AsyncLocal mutations made
        // downstream once the pipeline unwinds.
        context.Items[ChainTraceContext.HTTP_ITEM_KEY] = tracing.GetCurrentChain();
        try
        {
            await next(context);
            if (context.Response.StatusCode >= 400)
            {
                scope.EndWithFailure($"HTTP {context.Response.StatusCode}");
            }
            else
            {
                scope.EndWithSuccess();
            }
        }
        catch (Exception exception)
        {
            scope.EndWithException(exception);
            throw;
        }
    }
}
