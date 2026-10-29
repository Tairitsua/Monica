using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Monica.Repository.Persistence.Services.Support;

internal interface IRepositoryContextLifetime
{
    void ValidateDatabaseAccess();
    void MarkDatabaseFailure();
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

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => (eventData.Context as IRepositoryContextLifetime)?.MarkDatabaseFailure();

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        CommandFailed(command, eventData);
        return Task.CompletedTask;
    }

    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData)
        => (eventData.Context as IRepositoryContextLifetime)?.MarkDatabaseFailure();

    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        CommandCanceled(command, eventData);
        return Task.CompletedTask;
    }
}
