using Monica.Core.Execution;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Framework.ChainTracing.Extensions;
using Monica.Framework.ChainTracing.Models;
using Monica.Framework.ChainTracing.Services.Support;
using Monica.Tool.Extensions;
using Monica.Invocation.Abstractions;

namespace Monica.Framework.ChainTracing.Providers.Execution;

/// <summary>
/// Records business executions, including Task-based event handlers and jobs, in the ambient chain.
/// An execution without an inherited chain owns a new chain and completes it on every terminal path.
/// Nested executions leave their caller's chain lifetime unchanged.
/// </summary>
public sealed class ChainTracingExecutionBehavior<TInput, TResult>(IChainTracing chainTracing)
    : IExecutionBehavior<TInput, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> ExecuteAsync(
        ExecutionContext<TInput> context,
        ExecutionDelegate<TResult> next)
    {
        var descriptor = context.Descriptor;
        var isRemoteCall = typeof(IApplicationApi).IsAssignableFrom(descriptor.ComponentType)
                           || (descriptor.EntryMethod?.DeclaringType is { } declaringType
                               && typeof(IApplicationApi).IsAssignableFrom(declaringType));
        var traceType = isRemoteCall ? EChainTracingType.RemoteService : EChainTracingType.Unknown;

        var ownsChain = chainTracing.GetCurrentChain() is null;
        using var scope = chainTracing.BeginScope(
            descriptor.DisplayName,
            descriptor.ComponentType.Name,
            type: traceType);
        var ownedChain = ownsChain ? chainTracing.GetCurrentChain() : null;

        try
        {
            var result = await next();
            if (result is not IResultEnvelope response)
            {
                if (result is IExecutionOutcome { ShouldRollback: true }) scope.EndWithFailure("Failed");
                else scope.EndWithSuccess("Success");
                return result;
            }

            var responseTypeName = ChainTracingResultHelper.GetResponseTypeName(typeof(TResult));
            var description =
                $"{responseTypeName}({response.Status})" +
                (response.Message?.LimitMaxLength(1000, "...").BeNullIfWhiteSpace() is { } message
                    ? $"[{message}]"
                    : null);

            if (isRemoteCall)
            {
                scope.MergeRemoteChain(response);
            }

            if (response.IsOk())
            {
                scope.EndWithSuccess(description);
            }
            else
            {
                scope.EndWithFailure(description);
            }

            return result;
        }
        catch (OperationCanceledException exception)
        {
            scope.EndWithException(exception, $"Invoking {descriptor.DisplayName} was canceled.");
            throw;
        }
        catch (Exception exception)
        {
            scope.EndWithException(
                exception,
                $"Invoking {descriptor.DisplayName} failed.");
            throw;
        }
        finally
        {
            ownedChain?.MarkComplete();
        }
    }
}
