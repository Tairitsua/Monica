using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Pipeline;

namespace Monica.DataChannel;

/// <summary>
/// Represents a data channel.
/// Wraps a data pipeline and provides a unified access and control surface.
/// Acts as the managed unit exposed by the current host's <see cref="IDataChannelManager"/>.
/// </summary>
/// <param name="pipeline">The data pipeline instance.</param>
public class DataChannel(ChannelPipeline pipeline)
{
    internal Func<CancellationToken, Task>? Reinitializer { get; init; }
    /// <summary>
    /// Gets the underlying data pipeline.
    /// Contains the core data transfer and processing logic.
    /// </summary>
    public ChannelPipeline Pipe { get; } = pipeline;

    /// <summary>
    /// Gets the unique identifier of the data channel.
    /// This value matches the pipeline identifier.
    /// </summary>
    public string Id => Pipe.Id;

    /// <summary>
    /// Reinitializes the data channel.
    /// Call this when the channel needs to be reset or reconnected.
    /// Concurrent attempts are rejected. Success clears initialization unavailability; failure or cancellation
    /// retires the attempt with an uninitialized, unavailable pipeline. Endpoints remain host-owned.
    /// </summary>
    /// <param name="cancellationToken">Cancels this admitted endpoint initialization attempt.</param>
    /// <exception cref="InvalidOperationException">An initialization attempt is already active.</exception>
    /// <exception cref="OperationCanceledException">The supplied token cancels initialization.</exception>
    public async Task ReInitialize(CancellationToken cancellationToken = default)
    {
        if (Reinitializer is { } reinitialize) await reinitialize(cancellationToken);
        else await Pipe.InitAsync(cancellationToken);
    }

    /// <summary>
    /// Sends data from the inner endpoint.
    /// The payload passes through transform middleware, if any, and is then received by the outer endpoint.
    /// </summary>
    /// <param name="data">The data to send.</param>
    /// <param name="cancellationToken">Cancels the delivery; cancellation does not prove that no bytes were written.</param>
    public async Task SendDataFromInnerAsync(object data, CancellationToken cancellationToken = default)
    {
        await Pipe.SendDataAsync(new ChannelDataContext(ChannelSide.Inner, data) { CancellationToken = cancellationToken });
    }

    /// <summary>
    /// Sends data from the outer endpoint.
    /// The payload passes through transform middleware, if any, and is then received by the inner endpoint.
    /// </summary>
    /// <param name="data">The data to send.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    public async Task SendDataFromOuterAsync(object data, CancellationToken cancellationToken = default)
    {
        await Pipe.SendDataAsync(new ChannelDataContext(ChannelSide.Outer, data) { CancellationToken = cancellationToken });
    }
}
