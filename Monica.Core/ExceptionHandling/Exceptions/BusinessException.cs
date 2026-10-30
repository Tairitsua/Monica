namespace Monica.Core.ExceptionHandling.Exceptions;

/// <summary>
/// Represents an operation rejected by a business rule.
/// </summary>
/// <remarks>
/// The HTTP exception handler projects the message as a failed result. This type does not select an EventBus
/// retry or dead-letter policy; distributed consumers apply their configured retry budget to every processing failure.
/// </remarks>
/// <param name="message">The business rule that prevented the operation.</param>
public class BusinessException(string? message) : Exception(message)
{
}
