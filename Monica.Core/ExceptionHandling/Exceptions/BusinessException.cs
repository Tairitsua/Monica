namespace Monica.Core.ExceptionHandling.Exceptions;

/// <summary>
/// Represents a business-rule rejection that retrying the same operation cannot resolve.
/// </summary>
/// <remarks>
/// Built-in distributed EventBus consumers treat this exception as a permanent delivery failure after the handler
/// operation has unwound: they log the failure and discard the delivery, or use the provider's configured dead-letter
/// destination. Local EventBus calls still propagate it to their caller. Do not use this type for transient dependency
/// failures, timeouts, or cancellation.
/// </remarks>
/// <param name="message">The business rule that prevented the operation.</param>
public class BusinessException(string? message) : Exception(message)
{
}
