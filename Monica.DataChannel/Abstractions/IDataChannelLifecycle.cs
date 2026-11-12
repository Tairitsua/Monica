using Monica.DataChannel.Models;

namespace Monica.DataChannel.Abstractions;

/// <summary>
/// Controls selected host-owned setups without enabling other autonomous resources.
/// Commands reject overlap and disabled owners. Shutdown cancels startup and drains cleanup.
/// </summary>
public interface IDataChannelLifecycle
{
    /// <summary>Reads detached setup observations without constructing held setups.</summary>
    /// <returns>Every setup registered in this host, including held and disabled entries.</returns>
    IReadOnlyList<DataChannelSetupSnapshot> GetSetups();

    /// <summary>
    /// Materializes a held setup and initializes its channels. Running setups are unchanged.
    /// Failure or cancellation stops the setup's channels. Runtime-first HTTP mapping is unsupported.
    /// </summary>
    /// <param name="setupId">The exact identity returned by <see cref="GetSetups"/>.</param>
    /// <param name="cancellationToken">Cancels admission and startup; rollback always drains.</param>
    /// <returns>A task completed after initialization or rollback finishes.</returns>
    Task StartSetupAsync(string setupId, CancellationToken cancellationToken = default);

    /// <summary>Stops endpoints while retaining definitions and diagnostic history.</summary>
    /// <param name="setupId">The exact setup identity.</param>
    /// <param name="cancellationToken">Cancels admission; admitted cleanup cannot be abandoned.</param>
    /// <returns>A task completed after endpoint cleanup finishes.</returns>
    Task StopSetupAsync(string setupId, CancellationToken cancellationToken = default);

    /// <summary>Stops and starts a setup using existing definitions and frozen configuration.</summary>
    /// <param name="setupId">The exact setup identity.</param>
    /// <param name="cancellationToken">Cancels admission and startup; cleanup always drains.</param>
    /// <returns>A task completed after cleanup and replacement initialization finish.</returns>
    Task RestartSetupAsync(string setupId, CancellationToken cancellationToken = default);
}
