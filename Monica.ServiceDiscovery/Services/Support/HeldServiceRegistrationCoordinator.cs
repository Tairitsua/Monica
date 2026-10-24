using Monica.ServiceDiscovery.Abstractions;

namespace Monica.ServiceDiscovery.Services.Support;

/// <summary>Reports intentional nonparticipation immediately without constructing registry clients.</summary>
internal sealed class HeldServiceRegistrationCoordinator : IServiceRegistrationCoordinator
{
    public bool IsRegistered => false;

    public Task<bool> WaitForRegistrationAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}
