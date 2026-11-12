using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Monica.Repository.Persistence.Services.Support;

internal interface IRepositoryContextLifetime
{
    Type RegistrationType { get; }
    IServiceProvider Services { get; }
    void ValidateDatabaseAccess();
    void PrepareNativeWrite(IReadOnlyCollection<Type> affectedEntityTypes);
}

/// <summary>
/// Guards native LINQ, raw SQL and set-based writes as well as SaveChanges. A terminal operation's
/// context must not silently execute an autocommitted command after its transaction has been disposed.
/// </summary>
internal sealed class RepositoryOperationInterceptor : DbCommandInterceptor
{
    public static RepositoryOperationInterceptor Instance { get; } = new();

    private static void Validate(CommandEventData eventData)
    {
        if (eventData.Context is IRepositoryContextLifetime lifetime) lifetime.ValidateDatabaseAccess();
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Validate(eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ReaderExecuting(command, eventData, result));

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Validate(eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(NonQueryExecuting(command, eventData, result));

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Validate(eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ScalarExecuting(command, eventData, result));
}
