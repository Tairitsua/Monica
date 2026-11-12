namespace Monica.ServiceDiscovery.Abstractions;

/// <summary>
/// Coordinates service registration lifecycle and provides synchronization for dependent services
/// </summary>
public interface IServiceRegistrationCoordinator
{
    /// <summary>
    /// Gets whether the service is successfully registered
    /// </summary>
    bool IsRegistered { get; }

    /// <summary>
    /// Waits for registration to complete or timeout
    /// </summary>
    /// <param name="timeout">Maximum time to wait for registration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if registration completed successfully; false on timeout, failure or intentional Manual nonparticipation. Manual returns immediately without contacting the registry.</returns>
    Task<bool> WaitForRegistrationAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
