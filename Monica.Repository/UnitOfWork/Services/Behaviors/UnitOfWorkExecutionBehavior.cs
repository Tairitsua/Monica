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
        context.Features.TryGet<UnitOfWorkScopeOptions>(out var options);
        if (options is null && context.Descriptor.TransactionDbContextTypes is { } selected)
        {
            options = new UnitOfWorkScopeOptions(selected);
        }
        if (options is null && unitOfWorkManager.Current is null &&
            services?.GetService(typeof(ApplicationTransactionContexts)) is ApplicationTransactionContexts defaults &&
            defaults.Select(context.Descriptor) is { } ownedContexts)
            options = new UnitOfWorkScopeOptions(ownedContexts);
        return unitOfWorkManager.RunAsync(next.Invoke, options, context.CancellationToken);
    }
}
