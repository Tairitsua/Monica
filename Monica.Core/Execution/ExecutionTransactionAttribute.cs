using System.Data;

namespace Monica.Core.Execution;

/// <summary>
/// Overrides automatic unit-of-work participation and optional database context selection on an execution entry method.
/// </summary>
/// <remarks>
/// Without this attribute, the execution adapter supplies the default transaction mode. Automatic saving and commit
/// require the registered unit-of-work behavior and participating relational contexts. Use
/// <see cref="ExecutionTransactionMode.None"/> for operations that need no automatic outer unit of work, including
/// orchestration that resolves each independently committed child operation from a fresh service scope. This does not
/// suppress an already active transaction. Apply the declaration to the concrete handler method or MVC action;
/// declarations on overridden base methods are inherited.
/// </remarks>
/// <param name="mode">The transaction policy that overrides this method's execution default.</param>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class ExecutionTransactionAttribute(ExecutionTransactionMode mode) : Attribute
{
    /// <summary>Gets the transaction policy owned by this boundary.</summary>
    public ExecutionTransactionMode Mode { get; } = mode;

    /// <summary>
    /// Gets or sets the ordered database context types for an automatic unit of work. Defaults to an empty array,
    /// which uses the registered default context selection.
    /// </summary>
    /// <remarks>
    /// The first context owns the transaction and any Inbox or Outbox rows. Additional contexts must share the same
    /// connection instance and relational provider. The persistence module validates context registration and provider
    /// support. Types must be non-null and distinct; selection cannot be combined with
    /// <see cref="ExecutionTransactionMode.None"/>. Explicit execution scope options take precedence over this selection.
    /// </remarks>
    public Type[] DbContextTypes { get; set; } = [];

    /// <summary>Gets or sets the required isolation. Unspecified, the default, uses the provider's default.</summary>
    /// <remarks>
    /// Requires Automatic. A nested declaration must match the already-open transaction's isolation;
    /// it cannot upgrade or replace that transaction. Explicit execution scope options take precedence.
    /// </remarks>
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.Unspecified;
}
