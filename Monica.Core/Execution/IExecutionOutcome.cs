namespace Monica.Core.Execution;

/// <summary>Allows an adapter to report a returned failure to execution diagnostics without controlling transactions.</summary>
public interface IExecutionOutcome
{
    /// <summary>True when the operation returned a failed outcome. This does not request rollback.</summary>
    bool IsFailure { get; }
}
