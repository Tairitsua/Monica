using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Modules;
using Monica.Invocation.Abstractions;
using Monica.Invocation.Exceptions;
using Monica.Invocation.Models;
using Monica.Invocation.Services;

namespace Monica.Invocation.Facades;

/// <summary>Owns a complete remote exchange and projects classified failures into the declared result.</summary>
internal sealed class RemoteCallFacade(
    RemoteResponseDecoder decoder,
    IResultErrorMessageProvider messages,
    IJsonSerializerOptionsProvider serializer,
    IOptions<ModuleApiClientOption> options,
    IOptions<ModuleResultEnvelopeOption> envelopeOptions,
    TimeProvider timeProvider,
    ILogger<RemoteCallFacade> logger) : IRemoteCallClient
{
    public async Task<TResponse> InvokeAsync<TResponse>(HttpClient client,
        Func<CancellationToken, ValueTask<HttpRequestMessage>> createRequest,
        RemoteCallContext context, CancellationToken cancellationToken = default)
        where TResponse : class, IRemoteResultEnvelope<TResponse>
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(createRequest);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // ActivitySource.StartActivity can return null without listeners. This activity always owns a trace.
        using var activity = new Activity("Monica.RemoteCall").SetIdFormat(ActivityIdFormat.W3C).Start();
        var traceId = ResultTraceId.Capture();
        var started = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(options.Value.CallTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var stage = "request";
        try
        {
            using var request = await createRequest(linked.Token);
            stage = "send";
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            stage = "response";
            var result = await decoder.ReadAsync<TResponse>(response, context.Transport, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            IdentifyForwardedOrigin(result, context);
            // Target identity for the call-chain node regardless of outcome; presentation keeps this
            // reserved member only on hosts that expose diagnostic details.
            result.SetMetadata(ResultMetadataKeys.RemoteService, context.Service.Name);
            // Diagnostic hosts keep a downstream's reserved details (for example its metadata.exception stack)
            // flowing; every other host still strips them at this boundary.
            result.PrepareForPresentation(serializer.SerializerOptions, messages, traceId, context.Service.Name,
                context.Operation, exposeReservedDiagnostics: envelopeOptions.Value.ExposeDiagnosticDetails);
            activity.SetStatus(result.IsOk() ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested ||
            stage == "send" && exception.InnerException is TimeoutException)
        {
            return Failure<TResponse>(new RemoteCallFailure(ResStatus.GatewayTimeout,
                ResultErrorCodes.DependencyTimeout, stage), exception);
        }
        catch (RemoteCallException exception) when (stage == "response")
        {
            return Failure<TResponse>(exception.Failure, exception.InnerException);
        }
        catch (HttpRequestException exception) when (stage == "send" && !HasSerializationFailure(exception))
        {
            return Failure<TResponse>(new RemoteCallFailure(ResStatus.ServiceUnavailable,
                ResultErrorCodes.DependencyUnavailable, stage, exception.StatusCode), exception);
        }

        TEnvelope Failure<TEnvelope>(RemoteCallFailure failure, Exception? exception)
            where TEnvelope : class, IRemoteResultEnvelope<TEnvelope>
        {
            cancellationToken.ThrowIfCancellationRequested();
            activity.SetStatus(ActivityStatusCode.Error);
            activity.SetTag("error.type", failure.Code);
            var error = new ResultError(failure.Code, traceId, context.Service.Name, context.Operation);
            logger.LogWarning(
                "Remote call failed: {ErrorCode}; service {Service}; operation {Operation}; route {RouteTemplate}; stage {Stage}; transport {Transport}; HTTP {UpstreamStatus}; RetryAfter {RetryAfter}; duration {DurationMs} ms; exception {ExceptionType}; trace {TraceId}",
                failure.Code, context.Service.Name, context.Operation, context.RouteTemplate, failure.Stage, context.Transport,
                (int?)failure.UpstreamStatus, failure.RetryAfter, timeProvider.GetElapsedTime(started).TotalMilliseconds,
                exception?.GetType().Name, traceId);
            // The target identity stays on transport failures too: "which service was unreachable" is
            // exactly what the call chain needs to answer.
            return TEnvelope.CreateRemoteFailure(failure.Status, messages.GetMessage(error))
                .SetError(error)
                .SetMetadata(ResultMetadataKeys.RemoteService, context.Service.Name);
        }
    }

    private void IdentifyForwardedOrigin<TResponse>(TResponse result, RemoteCallContext context)
        where TResponse : class, IRemoteResultEnvelope<TResponse>
    {
        // A forwarded application failure keeps the origin's own error untouched; only an error that does not
        // identify its origin gains the immediate dependency so clients can show where the failure came from.
        if (result.IsOk() || !result.TryGetError(serializer.SerializerOptions, out var forwarded) ||
            forwarded.Service is not null || forwarded.Operation is not null) return;
        result.SetError(new ResultError(forwarded.Code, forwarded.TraceId,
            context.Service.Name, context.Operation, forwarded.Fields));
    }

    private static bool HasSerializationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is JsonException or NotSupportedException) return true;
        return false;
    }
}
