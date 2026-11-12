using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Execution;
using Monica.Invocation.Models;

namespace Monica.Invocation.Services;

internal static class ApiTransactionPolicy
{
    public static void Validate(ApiTransaction transaction)
    {
        if (!Enum.IsDefined(transaction)) throw new ArgumentOutOfRangeException(nameof(transaction));
    }

    public static async Task<T> ExecuteAsync<T>(IServiceProvider services, ApiTransaction transaction, Func<Task<T>> invoke,
        bool remote = false)
    {
        Validate(transaction);
        var boundary = GetActiveUnitOfWork(services, transaction);
        if (boundary?.FirstFailure is not null) boundary.Abort();
        if (remote) EnsureRemoteAllowed(services, transaction);
        var result = await invoke();
        if (boundary?.FirstFailure is not null) boundary.Abort();
        return result;
    }

    public static void EnsureRemoteAllowed(IServiceProvider services, ApiTransaction transaction)
    {
        if (GetActiveUnitOfWork(services, transaction) is { IsActive: true })
            throw new InvalidOperationException("A Shared API call cannot cross a remote boundary while a local transaction is active. " +
                "Use WithTransaction(ApiTransaction.Independent) when independent commit is intended.");
    }

    private static IExecutionTransaction? GetActiveUnitOfWork(IServiceProvider services, ApiTransaction transaction)
    {
        if (transaction != ApiTransaction.Shared) return null;
        var activeServices = services.GetRequiredService<ExecutionScopeContext>().GetActiveUnitOfWorkServices(services);
        return activeServices?.GetRequiredService<IExecutionTransaction>();
    }
}
