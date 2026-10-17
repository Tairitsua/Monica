namespace Monica.Core.Execution;

/// <summary>
/// Defines the standard order bands for execution behaviors. Lower values wrap higher values.
/// </summary>
public static class ExecutionBehaviorOrder
{
    /// <summary>
    /// Diagnostics, tracing, and metrics that should observe the complete execution.
    /// </summary>
    public const int Diagnostics = -3000;

    /// <summary>
    /// Authentication and authorization checks.
    /// </summary>
    public const int Authorization = -2000;

    /// <summary>
    /// Routing or remote-execution behaviors that may short-circuit local work.
    /// </summary>
    public const int Routing = -1000;

    /// <summary>Authoritative application-input validation before opening a unit of work.</summary>
    public const int Validation = -500;

    /// <summary>
    /// Transaction and unit-of-work boundaries around local application work.
    /// </summary>
    public const int UnitOfWork = 0;

    /// <summary>
    /// Default starting point for application-defined extension behaviors.
    /// </summary>
    public const int Application = 1000;
}
