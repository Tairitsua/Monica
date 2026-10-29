using Monica.Core.Execution;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Models;

namespace Monica.Repository.UnitOfWork.Services.Behaviors;

/// <summary>
/// Executes one business operation inside the current scope's Monica transaction.
/// </summary>
/// <remarks>
/// Nested execution adapters join the current unit of work. The outermost behavior owns the commit; failure handling
/// is delegated to <see cref="IUnitOfWorkManager.RunAsync{T}(Func{Task{T}},Monica.Repository.UnitOfWork.Models.UnitOfWorkScopeOptions?,CancellationToken)"/>.
/// </remarks>
public sealed class UnitOfWorkExecutionBehavior<TInput, TResult>(IUnitOfWorkManager unitOfWorkManager, IServiceProvider? services = null)
    : IExecutionBehavior<TInput, TResult>
{
    /// <inheritdoc />
    public Task<TResult> ExecuteAsync(
        ExecutionContext<TInput> context,
        ExecutionDelegate<TResult> next)
    {
        // None does not open a transaction, but nested entries still validate participation and failures.
        if (context.Descriptor.TransactionMode == ExecutionTransactionMode.None && unitOfWorkManager.Current is null)
            return next();
        context.Features.TryGet<UnitOfWorkScopeOptions>(out var options);
        if (options is null)
        {
            var selected = context.Descriptor.TransactionDbContextTypes;
            if (selected is null && services?.GetService(typeof(ApplicationTransactionContexts)) is ApplicationTransactionContexts defaults)
                selected = defaults.Select(context.Descriptor);
            if (selected is not null || context.Descriptor.TransactionIsolationLevel is not null)
                options = new UnitOfWorkScopeOptions(selected, context.Descriptor.TransactionIsolationLevel);
        }
        if (context.Descriptor.IsReadOnly)
            options = (options ?? new UnitOfWorkScopeOptions()) with { IsReadOnly = true };
        return unitOfWorkManager.RunAsync(next.Invoke, options, context.CancellationToken);
    }
}
