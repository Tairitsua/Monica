using Monica.DataChannel.Pipeline;

namespace Monica.DataChannel.Dapr.Abstractions;

/// <summary>
/// Allows a Dapr input binding to hand an incoming message to a custom dispatcher before the pipeline continues.
/// The host owns the dispatcher; it must be safe for concurrent callbacks. Returning success acknowledges the
/// Dapr callback, so processing must finish or durable acceptance must complete before returning <see langword="true"/>.
/// </summary>
public interface IDaprBindingInputDispatcher
{
    /// <summary>
    /// Attempts to dispatch the incoming input-binding payload.
    /// Return <see langword="true"/> when the dispatcher has handled the message or has accepted responsibility for it.
    /// Return <see langword="false"/> to let the normal pipeline run.
    /// </summary>
    /// <param name="context">The incoming channel data context.</param>
    /// <param name="next">
    /// The existing pipeline continuation. Its cancellation argument becomes the delivery token on the supplied
    /// context, including a replacement context; pass a linked token when dispatch work also observes another lifetime.
    /// </param>
    /// <param name="cancellationToken">A cancellation token for the current HTTP request.</param>
    /// <returns><see langword="true"/> when the message was handled by the dispatcher; otherwise <see langword="false"/>.</returns>
    Task<bool> TryDispatchAsync(
        ChannelDataContext context,
        Func<ChannelDataContext, CancellationToken, Task> next,
        CancellationToken cancellationToken = default);
}
