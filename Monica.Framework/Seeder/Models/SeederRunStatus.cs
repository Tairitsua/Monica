namespace Monica.Framework.Seeder.Models;

/// <summary>
/// Describes the current lifecycle state of the host-owned seeder run.
/// </summary>
public enum SeederRunStatus
{
    /// <summary>The scheduler is waiting for the host to finish starting.</summary>
    Waiting,

    /// <summary>The scheduler is executing the dependency graph.</summary>
    Running,

    /// <summary>A fail-fast failure is cancelling and draining in-flight work.</summary>
    Aborting,

    /// <summary>All admitted work succeeded and no held definitions remain; disabled definitions were not executed.</summary>
    Succeeded,

    /// <summary>The run finished without fail-fast, but at least one seeder was unsuccessful.</summary>
    CompletedWithFailures,

    /// <summary>The run was aborted by a seeder whose effective failure behavior is fail-fast.</summary>
    Aborted,

    /// <summary>The host cancelled the run before normal completion.</summary>
    Cancelled,

    /// <summary>No batch is executing; some seeders remain intentionally held and have not succeeded.</summary>
    Held
}
