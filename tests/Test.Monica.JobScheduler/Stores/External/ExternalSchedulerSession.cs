using System.Data.Common;
using GaussDB;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>
/// Identifies one physical database session even when Vastbase moves it between worker threads.
/// </summary>
internal static class ExternalSchedulerSession
{
    internal static string LockIdentityColumn(DbConnection connection) => connection is GaussDBConnection ? "sessionid" : "pid";

    internal static async Task<long> ReadIdAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        // Read before BEGIN: pg_stat_activity snapshots can otherwise be cached for the transaction.
        command.CommandText = connection is GaussDBConnection
            ? "SELECT sessionid FROM pg_catalog.pg_stat_activity WHERE pid = pg_backend_pid()"
            : "SELECT pg_backend_pid()";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("The provider returned no session identity."));
    }
}
