using Monica.Core.Mediator;
using Monica.Core.Results.Abstractions;

namespace Monica.Invocation.Abstractions;

/// <summary>Dispatches generated Local calls through the host's operation boundary.</summary>
/// <remarks>Generated clients resolve this scoped service at invocation time. Application hosts supply scope and caller-context handling.</remarks>
public interface ILocalApiInvoker
{
    /// <summary>Invokes an operation with caller cancellation. Nested application calls retain their active execution scope.</summary>
    Task<TResult> InvokeAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default)
        where TResult : class, IRemoteResultEnvelope<TResult>;
}
