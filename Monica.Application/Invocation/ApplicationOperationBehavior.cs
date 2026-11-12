using Monica.Core.Execution;

namespace Monica.Application.Invocation;

/// <summary>Makes every application business entry's receiving scope available to nested API calls.</summary>
internal sealed class ApplicationOperationBehavior<TInput, TResult>(
    IServiceProvider services, ExecutionScopeContext operation) : IExecutionBehavior<TInput, TResult>
{
    public async Task<TResult> ExecuteAsync(ExecutionContext<TInput> context, ExecutionDelegate<TResult> next)
    {
        using var lease = operation.Enter(services);
        return await next();
    }
}
