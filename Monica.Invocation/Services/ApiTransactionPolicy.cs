using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Execution;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Invocation.Models;

namespace Monica.Invocation.Services;

internal static class ApiTransactionPolicy
{
    public static void Validate(ApiTransaction transaction)
    {
        if (!Enum.IsDefined(transaction)) throw new ArgumentOutOfRangeException(nameof(transaction));
    }

    public static async Task<T> ExecuteAsync<T>(IServiceProvider services, ApiTransaction transaction, Func<Task<T>> invoke,
        bool remote = false, bool isReadOnly = false, string? operation = null)
    {
        Validate(transaction);
        var boundary = GetActiveTransaction(services, transaction);
        try
        {
            if (remote) EnsureRemoteAllowed(services, transaction);
            var result = await invoke();
            if (ExecutionOutcomePolicy.RequiresRollback(result, isReadOnly))
                boundary?.MarkRollbackOnly(ExecutionTransactionFailure.FromResult("ApiInvocation", result) with { Operation = operation });
            return result;
        }
        catch (Exception exception)
        {
            boundary?.MarkRollbackOnly(ExecutionTransactionFailure.FromException("ApiInvocation", exception) with { Operation = operation });
            throw;
        }
    }

    public static void EnsureRemoteAllowed(IServiceProvider services, ApiTransaction transaction)
    {
        if (GetActiveTransaction(services, transaction) is not null)
            throw new InvalidOperationException("A Shared API call cannot cross a remote boundary while a local transaction is active. " +
                "Use WithTransaction(ApiTransaction.Independent) when independent commit is intended.");
    }

    private static IExecutionTransaction? GetActiveTransaction(IServiceProvider services, ApiTransaction transaction)
    {
        if (transaction != ApiTransaction.Shared) return null;
        var activeServices = services.GetService<ExecutionScopeContext>()?.Services ?? services;
        return activeServices.GetService<IExecutionTransaction>() is { IsActive: true } boundary ? boundary : null;
    }
}
