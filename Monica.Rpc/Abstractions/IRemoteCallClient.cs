using Monica.Core.Results.Abstractions;
using Monica.Rpc.Models;

namespace Monica.Rpc.Abstractions;

/// <summary>Executes one HTTP exchange using Monica's result contract.</summary>
public interface IRemoteCallClient
{
    /// <summary>Creates, sends, reads, and disposes one request and response within a single deadline.</summary>
    /// <param name="client">Borrowed client. Its timeout is never modified or assumed to be infinite.</param>
    /// <param name="createRequest">Called at most once; must honor its cancellation token and transfers request/content ownership on successful return.</param>
    /// <param name="context">Host-selected target, operation, and transport.</param>
    /// <param name="cancellationToken">Caller cancellation, propagated without a fabricated error result.</param>
    /// <returns>The application envelope or a classified dependency failure.</returns>
    /// <exception cref="OperationCanceledException">The caller or an unrelated provider canceled execution.</exception>
    /// <remarks>Local request/configuration defects propagate. Requests are never retried.</remarks>
    Task<TResponse> InvokeAsync<TResponse>(HttpClient client,
        Func<CancellationToken, ValueTask<HttpRequestMessage>> createRequest,
        RemoteCallContext context, CancellationToken cancellationToken = default)
        where TResponse : class, IRemoteResultEnvelope<TResponse>;
}
