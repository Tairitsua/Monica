using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Locker.Leases;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Events;
using Monica.ServiceDiscovery.Models;

namespace Monica.ServiceDiscovery.Services.Support;

/// <summary>Supervises application leadership independently of membership heartbeat I/O.</summary>
/// <remarks>
/// Each acquisition publishes a fresh cancellation token. Local resignation stops renewal and leaves the grant to
/// expire: event subscribers own asynchronous work that cannot be proven drained by this service. No normal shutdown
/// deletes a role. External effects still require idempotency or resource-side fencing, especially with Redis failover.
/// </remarks>
public sealed class LeaderElectionService(
    IServiceDiscoveryClientInfo clientInfo,
    IOptions<ModuleServiceDiscoveryOption> options,
    ILogger<LeaderElectionService> logger,
    [FromKeyedServices(nameof(ModuleServiceDiscovery))] IExclusiveLeaseStore leases,
    TimeProvider timeProvider) : ILeaderElectionService, IDisposable
{
    private readonly ModuleServiceDiscoveryOption _options = options.Value;
    private readonly object _gate = new();
    private LeaseCoordinator? _coordinator;
    private LeaseContext? _current;
    private DateTime? _becameLeader;
    private long? _registeredAt;
    private CancellationTokenRegistration _lossRegistration;
    private Task _notifications = Task.CompletedTask;
    private bool _disposed;

    /// <inheritdoc />
    public LeaseContext? CurrentLease
    {
        get { lock (_gate) return _current is { StoppingToken.IsCancellationRequested: false } ? _current : null; }
    }
    /// <inheritdoc />
    public LeaderStatus CurrentStatus => IsLeader ? LeaderStatus.Leader : LeaderStatus.Follower;
    /// <inheritdoc />
    public bool IsLeader => CurrentLease is not null;
    /// <inheritdoc />
    public DateTime? LeaderBecomeTime { get { lock (_gate) return IsLeader ? _becameLeader : null; } }
    /// <inheritdoc />
    public string? CurrentTermId => CurrentLease?.Identity.TermId;
    /// <inheritdoc />
    public event EventHandler<LeaderGainedEvent>? OnLeaderGained;
    /// <inheritdoc />
    public event EventHandler<LeaderLostEvent>? OnLeaderLost;

    internal void SetRegistrationAvailable(bool available)
    {
        lock (_gate) _registeredAt = available ? timeProvider.GetTimestamp() : null;
        if (!available) TriggerLeaderLost(LeaderLostReason.NetworkIsolation);
    }

    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_options.Role == ServiceDiscoveryRole.Worker) return;
        var status = clientInfo.GetServiceStatus();
        var resource = Resource(_options, status.AppId);
        var runtimeOptions = _options.Election.CreateLeaseOptions();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                LeaseCoordinator? coordinator;
                lock (_gate)
                {
                    if (_disposed) return;
                    coordinator = RegistrationIsCurrent()
                        ? _coordinator ??= new LeaseCoordinator(leases, status.InstanceId, timeProvider, logger, runtimeOptions)
                        : null;
                }
                if (coordinator is null) TriggerLeaderLost(LeaderLostReason.NetworkIsolation);
                else
                {
                    try
                    {
                        if (await coordinator.GetAsync(resource, stoppingToken) is { } term)
                            Publish(coordinator, term);
                    }
                    catch (ObjectDisposedException) { /* Membership loss or local resignation stopped this supervisor. */ }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Leadership acquisition failed for {AppId}.", status.AppId);
                    }
                }
                await Task.Delay(runtimeOptions.StandbyInterval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { TriggerLeaderLost(LeaderLostReason.GracefulShutdown); }
    }

    private bool RegistrationIsCurrent() => _registeredAt is { } registered
        && timeProvider.GetElapsedTime(registered) < _options.Election.RegistrationTTL;

    private void Publish(LeaseCoordinator coordinator, LeaseContext term)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_coordinator, coordinator) || !RegistrationIsCurrent()
                || term.StoppingToken.IsCancellationRequested || _current?.Identity == term.Identity) return;
            LoseCurrent(LeaderLostReason.LeaderKeyExpired);
            _current = term;
            _becameLeader = timeProvider.GetUtcNow().UtcDateTime;
            var status = clientInfo.GetServiceStatus();
            var gained = new LeaderGainedEvent
            {
                AppId = status.AppId, InstanceId = status.InstanceId, BecomeLeaderTime = _becameLeader.Value, Lease = term
            };
            Notify(OnLeaderGained, gained);
            _lossRegistration = term.StoppingToken.Register(() =>
            {
                lock (_gate)
                    if (_current?.Identity == term.Identity) LoseCurrent(LeaderLostReason.LeaderKeyExpired);
            });
        }
    }

    /// <inheritdoc />
    public void TriggerLeaderLost(LeaderLostReason reason)
    {
        LeaseCoordinator? coordinator;
        lock (_gate)
        {
            coordinator = _coordinator;
            _coordinator = null;
            LoseCurrent(reason);
        }
        // Do not release before asynchronous subscribers have drained. Expiry bounds takeover instead.
        coordinator?.Dispose();
    }

    private void LoseCurrent(LeaderLostReason reason)
    {
        if (_current is not { } previous) return;
        _lossRegistration.Unregister();
        _current = null;
        _becameLeader = null;
        var status = clientInfo.GetServiceStatus();
        Notify(OnLeaderLost, new LeaderLostEvent
        {
            AppId = status.AppId, InstanceId = status.InstanceId, Identity = previous.Identity,
            LostTime = timeProvider.GetUtcNow().UtcDateTime, Reason = reason
        });
    }

    private void Notify<T>(EventHandler<T>? handlers, T notification)
    {
        // Preserve gained/lost order without running application callbacks under the state lock.
        _notifications = _notifications.ContinueWith(_ =>
        {
            if (handlers is null) return;
            foreach (EventHandler<T> handler in handlers.GetInvocationList())
                try { handler(this, notification); }
                catch (Exception exception) { logger.LogError(exception, "Leadership observer failed."); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    internal const string RESOURCE_PREFIX = "service-discovery:";
    internal static LeaseResource Resource(ModuleServiceDiscoveryOption options, string appId) =>
        new(options.LeaderLeaseStore?.Scope ?? "service-discovery", RESOURCE_PREFIX + appId);

    /// <summary>Stops supervision and signals loss; the backend grant is left to expire.</summary>
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        TriggerLeaderLost(LeaderLostReason.GracefulShutdown);
    }
}
