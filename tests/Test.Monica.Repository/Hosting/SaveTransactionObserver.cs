using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Test.Monica.Repository.Hosting;

/// <summary>Observes transaction commands in repository save-boundary regression scenarios.</summary>
internal class SaveTransactionObserver : DbTransactionInterceptor
{
    public int Starts { get; private set; }
    public int Commits { get; private set; }
    public int Rollbacks { get; private set; }
    public int Savepoints { get; private set; }
    public int Releases { get; private set; }

    public void Reset()
        => (Starts, Commits, Rollbacks, Savepoints, Releases) = (0, 0, 0, 0, 0);

    public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection,
        TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        Starts++;
        return result;
    }

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
        TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(TransactionStarting(connection, eventData, result));

    public override InterceptionResult TransactionCommitting(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result)
    {
        Commits++;
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(TransactionCommitting(transaction, eventData, result));

    public override InterceptionResult TransactionRollingBack(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result)
    {
        Rollbacks++;
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(TransactionRollingBack(transaction, eventData, result));

    public override InterceptionResult CreatingSavepoint(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result)
    {
        Savepoints++;
        return result;
    }

    public override ValueTask<InterceptionResult> CreatingSavepointAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CreatingSavepoint(transaction, eventData, result));

    public override InterceptionResult ReleasingSavepoint(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result)
    {
        Releases++;
        return result;
    }

    public override ValueTask<InterceptionResult> ReleasingSavepointAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ReleasingSavepoint(transaction, eventData, result));
}
