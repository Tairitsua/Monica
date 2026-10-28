using Monica.Core.Mediator;
using Monica.Core.Results.Abstractions;
using Monica.Invocation.Models;

namespace Monica.Invocation.Abstractions;

/// <summary>Dispatches generated Local calls through the host's operation boundary.</summary>
/// <remarks>Generated clients resolve this scoped service at invocation time. Application hosts supply scope and caller-context handling.</remarks>
public interface ILocalApiInvoker
{
    /// <summary>Invokes an operation with caller cancellation and explicit transaction participation.</summary>
    /// <remarks>Shared retains the active scope; Independent resolves the target and its dependencies in a fresh scope.</remarks>
    Task<TResult> InvokeAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default,
        ApiTransaction transaction = ApiTransaction.Shared)
        where TResult : class, IRemoteResultEnvelope<TResult>;
}
