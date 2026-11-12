using Monica.JobScheduler.Models.Definitions;

namespace Monica.JobScheduler.Models;

/// <summary>
/// Binds one immutable code declaration to the CLR types available in the current host process.
/// </summary>
/// <remarks>
/// This model is process-local and is never persisted. Durable definition and execution records use stable string
/// identities so any host can present the full operational view without loading worker assemblies.
/// </remarks>
public sealed record LocalJobDefinition
{
    /// <summary>
    /// Gets the logical owner whose durable definition and executions this process can publish and execute.
    /// Module discovery freezes the application activation policy's owner, or the configured project owner.
    /// A manually constructed definition may leave this null to use the scheduler's project-owner fallback.
    /// </summary>
    public string? OwnerKey { get; init; }

    /// <summary>
    /// Gets the immutable declaration discovered from the worker assembly.
    /// </summary>
    public required JobDeclaration Declaration { get; init; }

    /// <summary>
    /// Gets the concrete job implementation type resolved from the worker dependency-injection scope.
    /// </summary>
    public required Type JobClrType { get; init; }

    /// <summary>
    /// Gets the triggered-job argument type, or <see langword="null"/> for a recurring job.
    /// </summary>
    public Type? JobArgsClrType { get; init; }
}
