using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Extensions;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Locker.Leases;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Events;

namespace Monica.ServiceDiscovery.Services.Support;

/// <summary>Runs application work for individual leadership terms after membership registration.</summary>
/// <remarks>
/// Work is linked directly to the term token, so slow initialization cannot delay loss cancellation. Transitions are
/// serialized: old work and its cleanup must finish before this service starts a later term. Implementations must
/// cooperate with cancellation; the host owns the shutdown deadline. Cancellation alone cannot fence external effects.
/// </remarks>
public abstract class CoordinatedLeaderService(
    ILeaderElectionService leaderService,
    IOptions<ModuleServiceDiscoveryOption> options,
    IServiceRegistrationCoordinator coordinator,
    IObservableInstanceRegistry observableManager,
    IOptions<ModuleHostedServiceOption> hostedServiceOptions,
    IServiceScopeFactory serviceScopeFactory,
    ILogger logger) : MoBackgroundService(observableManager, hostedServiceOptions, serviceScopeFactory, logger)
{
    /// <summary>Host-owned discovery configuration.</summary>
    protected readonly ModuleServiceDiscoveryOption Options = options.Value;
    /// <summary>Confirmed local leadership and term observations.</summary>
    protected readonly ILeaderElectionService LeaderService = leaderService;

    private readonly Channel<Transition> _transitions = Channel.CreateUnbounded<Transition>(new()
        { SingleReader = true, AllowSynchronousContinuations = false });
    private LeaseIdentity? _identity;
    private CancellationTokenSource? _leaderCts;
    private Task? _work;

    /// <summary>Whether initialization has completed without a current failure.</summary>
    public bool IsInitialized => RuntimeInfo.CurrentState is HostedServiceState.Running or HostedServiceState.Executing;
    /// <summary>Whether this host currently has a confirmed leadership term.</summary>
    public bool IsCurrentlyLeader => LeaderService.IsLeader;
    /// <inheritdoc />
    public override string? ServiceGroupId => nameof(ModuleServiceDiscovery);
    /// <summary>The most recent lifecycle failure, if any.</summary>
    public string? InitializationError => RuntimeInfo.StateHistory.Where(item => item.Exception is not null)
        .OrderByDescending(item => item.Timestamp).FirstOrDefault()?.Exception?.GetMessageRecursively();

    /// <inheritdoc />
    protected sealed override async Task ExecuteBackgroundAsync(CancellationToken stoppingToken)
    {
        try
        {
            await OnBeforeInitialization(stoppingToken);
            if (!Options.SkipRegistrationWait)
            {
                RecordState("Waiting for registration", HostedServiceState.Starting);
                var registered = await coordinator.WaitForRegistrationAsync(Options.RegistrationWaitTimeout, stoppingToken);
                if (!registered) RecordState("Registration wait timed out", HostedServiceState.Degraded);
            }
            LeaderService.OnLeaderGained += OnGained;
            LeaderService.OnLeaderLost += OnLost;
            if (LeaderService.CurrentLease is { } current) _transitions.Writer.TryWrite(new(current, null));
            RecordState("Waiting for leadership", HostedServiceState.Running);

            await foreach (var transition in _transitions.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (transition.Gained is { } term)
                    {
                        if (_identity == term.Identity || term.StoppingToken.IsCancellationRequested) continue;
                        await DrainAsync(LeaderLostReason.LeaderKeyExpired);
                        stoppingToken.ThrowIfCancellationRequested();
                        if (term.StoppingToken.IsCancellationRequested) continue;
                        _identity = term.Identity;
                        _leaderCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, term.StoppingToken);
                        var token = _leaderCts.Token;
                        RecordState("Initializing leadership term", HostedServiceState.Executing);
                        await OnBecameLeaderAsync(token);
                        token.ThrowIfCancellationRequested();
                        _work = Task.Run(() => ExecuteLeaderWorkAsync(token), CancellationToken.None);
                        RecordState("Leadership term initialized", HostedServiceState.Running);
                    }
                    else if (transition.Lost is { } lost && _identity == lost.Identity)
                        await DrainAsync(lost.Reason);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested
                    || _leaderCts?.IsCancellationRequested == true)
                {
                    await DrainAsync(LeaderLostReason.LeaderKeyExpired);
                }
                catch (Exception exception)
                {
                    RecordState("Leadership transition failed", HostedServiceState.Faulted, exception);
                    await DrainAsync(LeaderLostReason.LeaderKeyExpired);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            LeaderService.OnLeaderGained -= OnGained;
            LeaderService.OnLeaderLost -= OnLost;
            _transitions.Writer.TryComplete();
            await DrainAsync(LeaderLostReason.GracefulShutdown);
        }
    }

    private void OnGained(object? sender, LeaderGainedEvent notification) =>
        _transitions.Writer.TryWrite(new(notification.Lease, null));
    private void OnLost(object? sender, LeaderLostEvent notification) =>
        _transitions.Writer.TryWrite(new(null, notification));

    private async Task ExecuteLeaderWorkAsync(CancellationToken token)
    {
        try { await LeaderExecuteBackgroundAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { RecordState("Leader work failed", HostedServiceState.Faulted, exception); }
    }

    private async Task DrainAsync(LeaderLostReason reason)
    {
        if (_leaderCts is null) return;
        try
        {
            try { await _leaderCts.CancelAsync(); }
            catch (Exception exception) { Logger.LogError(exception, "Leader cancellation callback failed."); }
            if (_work is not null) await _work;
            await OnLeaderLostAsync(reason);
        }
        catch (Exception exception) { Logger.LogError(exception, "Leader cleanup failed."); }
        finally
        {
            _leaderCts.Dispose();
            _leaderCts = null;
            _identity = null;
            _work = null;
        }
    }

    /// <summary>Optional preparation before registration wait and term subscriptions.</summary>
    protected virtual Task OnBeforeInitialization(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <summary>Initializes one term. Must stop promptly when its token is cancelled, including during initialization.</summary>
    protected abstract Task OnBecameLeaderAsync(CancellationToken leaderToken);
    /// <summary>Runs work for one term. A later term cannot start in this service until this task completes.</summary>
    protected virtual Task LeaderExecuteBackgroundAsync(CancellationToken leaderToken) => Task.CompletedTask;
    /// <summary>Cleans up one completed term, after its initialization and background work have stopped.</summary>
    protected virtual Task OnLeaderLostAsync(LeaderLostReason reason) => Task.CompletedTask;

    private sealed record Transition(LeaseContext? Gained, LeaderLostEvent? Lost);
}
