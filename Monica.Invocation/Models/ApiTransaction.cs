namespace Monica.Invocation.Models;

/// <summary>Defines how one API invocation participates in its caller's transaction.</summary>
public enum ApiTransaction
{
    /// <summary>
    /// Joins the active local unit of work, or resolves a fresh operation scope when none is active. This is the default.
    /// The target's execution policy decides whether a new operation opens a transaction.
    /// An active transaction cannot cross an HTTP boundary or enlist an independent database connection.
    /// </summary>
    Shared,

    /// <summary>
    /// Executes in an independent operation and dependency scope. Successful writes remain committed if the
    /// caller later rolls back. The target's execution policy still decides whether it starts a transaction.
    /// </summary>
    Independent
}
