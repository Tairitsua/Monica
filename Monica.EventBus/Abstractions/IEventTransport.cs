using Monica.EventBus.Models;

namespace Monica.EventBus.Abstractions;

/// <summary>Sends a prepared distributed event without entering the application publishing gateway.</summary>
public interface IEventTransport
{
    /// <summary>
    /// Sends the stored identity, route, and body. Successful completion means transport acceptance.
    /// The task retains ownership until the underlying client operation finishes, including after cancellation;
    /// callers may stop waiting independently but must retain resources/capacity until this task completes.
    /// A failure or cancellation can leave acceptance unknown and must not imply that retry is duplicate-free.
    /// </summary>
    Task SendAsync(EventMessage message, CancellationToken cancellationToken);
}
