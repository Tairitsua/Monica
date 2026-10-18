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
/// Records result-envelope business executions in Monica's ambient chain trace.
/// </summary>
public sealed class ChainTracingExecutionBehavior<TInput, TResult>(IChainTracing chainTracing)
    : IExecutionBehavior<TInput, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> ExecuteAsync(
        ExecutionContext<TInput> context,
        ExecutionDelegate<TResult> next)
    {
        if (!typeof(IResultEnvelope).IsAssignableFrom(typeof(TResult)))
        {
            return await next();
        }

        var descriptor = context.Descriptor;
        var isRemoteCall = typeof(IApplicationApi).IsAssignableFrom(descriptor.ComponentType)
                           || (descriptor.EntryMethod?.DeclaringType is { } declaringType
                               && typeof(IApplicationApi).IsAssignableFrom(declaringType));
        var traceType = isRemoteCall ? EChainTracingType.RemoteService : EChainTracingType.Unknown;

        using var scope = chainTracing.BeginScope(
            descriptor.DisplayName,
            descriptor.ComponentType.Name,
            type: traceType);

        try
        {
            var result = await next();
            if (result is not IResultEnvelope response)
            {
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

            if (response.Status == ResStatus.Ok)
            {
                scope.EndWithSuccess(description);
            }
            else
            {
                scope.EndWithFailure(description);
            }

            return result;
        }
        catch (Exception exception)
        {
            scope.EndWithException(
                exception,
                $"Invoking {descriptor.DisplayName} failed.");
            throw;
        }
    }
}
