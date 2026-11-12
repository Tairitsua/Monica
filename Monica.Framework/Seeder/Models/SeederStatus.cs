namespace Monica.Framework.Seeder.Models;

/// <summary>
/// Describes the current lifecycle state of one discovered seeder.
/// </summary>
public enum SeederStatus
{
    /// <summary>The seeder is waiting for its dependencies and an execution slot.</summary>
    Pending,

    /// <summary>The seeder is executing or waiting to retry a failed attempt.</summary>
    Running,

    /// <summary>The seeder completed successfully.</summary>
    Succeeded,

    /// <summary>The seeder exhausted its configured attempts.</summary>
    Failed,

    /// <summary>The seeder could not run because one of its dependencies did not succeed.</summary>
    Blocked,

    /// <summary>The seeder was cancelled before or during execution.</summary>
    Cancelled,

    /// <summary>The seeder is available but awaits explicit admission or a held prerequisite.</summary>
    Held,

    /// <summary>The seeder belongs to another placement and cannot execute in this process.</summary>
    Disabled
}
