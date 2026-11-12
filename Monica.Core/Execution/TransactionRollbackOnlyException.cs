namespace Monica.Core.Execution;

/// <summary>Rejects further work on a failed transaction while preserving the failure that caused rollback.</summary>
/// <remarks>
/// The original exception is the inner exception, so existing exception diagnostics retain its original stack.
/// A failed result has no inner exception; its status and message remain available in <see cref="FirstFailure"/>.
/// </remarks>
public sealed class TransactionRollbackOnlyException(Guid transactionId, string rejectedOperation, ExecutionTransactionFailure firstFailure)
    : InvalidOperationException(
        $"Cannot {rejectedOperation}: transaction '{transactionId}' is rollback-only. " +
        $"First failure in '{firstFailure.Operation ?? "unspecified operation"}' ({firstFailure.Source})" +
        (firstFailure.ResultStatus is { } status ? $", status {status}" : "") +
        $": {firstFailure.Reason}" +
        (firstFailure.InvocationId is { } invocation ? $" InvocationId: {invocation}." : "") +
        (firstFailure.TraceId is { } trace ? $" TraceId: {trace}." : ""), firstFailure.Exception)
{
    /// <summary>Gets the identity of the transaction that cannot commit.</summary>
    public Guid TransactionId { get; } = transactionId;

    /// <summary>Gets the action rejected because the transaction was already failed.</summary>
    public string RejectedOperation { get; } = rejectedOperation;

    /// <summary>Gets the original failure snapshot; later failures never replace it.</summary>
    public ExecutionTransactionFailure FirstFailure { get; } = firstFailure;
}
