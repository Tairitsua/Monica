using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>
/// Records actual EF command execution and transaction starts without replacing provider behavior.
/// </summary>
internal sealed class ExternalStoreCommandCounter
{
    private readonly ConcurrentQueue<string> _commands = new();
    private int _transactionStarts;

    internal ExternalStoreCommandCounter()
    {
        Interceptors = [new Commands(this), new Transactions(this)];
    }

    internal IInterceptor[] Interceptors { get; }
    internal IReadOnlyList<string> CommandsExecuted => _commands.ToArray();
    internal int TransactionStarts => Volatile.Read(ref _transactionStarts);

    internal void Reset()
    {
        _commands.Clear();
        Interlocked.Exchange(ref _transactionStarts, 0);
    }

    private sealed class Commands(ExternalStoreCommandCounter owner) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            owner._commands.Enqueue(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            owner._commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            owner._commands.Enqueue(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            owner._commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            owner._commands.Enqueue(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            owner._commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Transactions(ExternalStoreCommandCounter owner) : DbTransactionInterceptor
    {
        public override InterceptionResult<DbTransaction> TransactionStarting(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            Interlocked.Increment(ref owner._transactionStarts);
            return result;
        }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner._transactionStarts);
            return ValueTask.FromResult(result);
        }
    }
}
