using Monica.Core.Execution;

namespace Monica.Application.Invocation;

/// <summary>Marks native mediator entries so nested Local APIs join the receiving operation and transaction.</summary>
internal sealed class ApplicationOperationBehavior<TInput, TResult>(
    IServiceProvider services, ApplicationOperationContext operation) : IExecutionBehavior<TInput, TResult>
{
    public async Task<TResult> ExecuteAsync(ExecutionContext<TInput> context, ExecutionDelegate<TResult> next)
    {
        using var lease = operation.Enter(services);
        return await next();
    }
}
