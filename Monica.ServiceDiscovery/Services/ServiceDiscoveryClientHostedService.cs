using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Services.Support;
using Polly;
using Polly.Registry;

namespace Monica.ServiceDiscovery.Services;

/// <summary>
/// Publishes StateStore membership through a retry pipeline and runs independent lease leadership supervision.
/// </summary>
public class ServiceDiscoveryClientHostedService(
    IRegistrationStateManager stateManager,
    LeaderElectionService leaderService,
    IServiceDiscoveryClientInfo clientInfo,
    IOptions<ModuleServiceDiscoveryOption> option,
    IObservableInstanceRegistry observableManager,
    IOptions<ModuleHostedServiceOption> hostedServiceOptions,
    ResiliencePipelineProvider<string> pipelineProvider,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<ServiceDiscoveryClientHostedService> logger,
    TimeProvider timeProvider)
    : MoBackgroundService(observableManager, hostedServiceOptions, serviceScopeFactory, logger), IServiceRegistrationCoordinator
{
    private readonly ModuleServiceDiscoveryOption _option = option.Value;
    private readonly TaskCompletionSource<bool> _registrationCompletionSource = new();
    private readonly Random _random = new();
    private readonly ResiliencePipeline _heartbeatPipeline = pipelineProvider.GetPipeline(ResiliencePipelineNames.ServiceDiscovery);

    private int _consecutiveFailures;

    public override string ServiceName => "ServiceDiscoveryClient";
    public override string? ServiceGroupId => nameof(ModuleServiceDiscovery);
    public override TimeSpan? HeartbeatInterval => null;

    public bool IsRegistered => RuntimeInfo.CurrentState == HostedServiceState.Running;

    public async Task<bool> WaitForRegistrationAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await _registrationCompletionSource.Task.WaitAsync(cts.Token);
            return _registrationCompletionSource.Task.Result;
        }
        catch (OperationCanceledException)
        {
            RecordState($"Waiting for registration timed out ({timeout})", logLevel: LogLevel.Warning);
            return false;
        }
    }

    protected override async Task ExecuteBackgroundAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = RunHeartbeatAsync(lifetime.Token);
        var election = _option.Role == ServiceDiscoveryRole.Worker
            ? Task.Delay(Timeout.Infinite, lifetime.Token) : leaderService.RunAsync(lifetime.Token);
        try { await await Task.WhenAny(heartbeat, election); }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(heartbeat, election);
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken stoppingToken)
    {
        RecordState("Starting StateStore heartbeat loop", HostedServiceState.Starting);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteHeartbeatWithResilienceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordState("Heartbeat loop exception", HostedServiceState.Degraded, ex);
            }

            // Wait with jitter
            var jitter = _random.Next(
                -_option.Election.HeartbeatJitterMilliseconds,
                _option.Election.HeartbeatJitterMilliseconds);
            var delay = _option.Election.HeartbeatPeriod + TimeSpan.FromMilliseconds(jitter);

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

    }

    /// <summary>
    /// Execute heartbeat with Polly resilience pipeline.
    /// The pipeline handles retries automatically.
    /// </summary>
    private async Task ExecuteHeartbeatWithResilienceAsync(CancellationToken ct)
    {
        try
        {
            RegistrationResult? result = null;
            await _heartbeatPipeline.ExecuteAsync(async token =>
            {
                result = await stateManager.RegisterOrHeartbeatAsync(token);
                if (!result.Success)
                {
                    throw new HeartbeatFailedException(result.ErrorMessage ?? "Heartbeat failed");
                }
            }, ct);

            // Heartbeat succeeded (possibly after retries)
            // Update client info with actual registration/heartbeat times
            if (result?.HeartbeatTime.HasValue == true)
            {
                clientInfo.SetRegistrationTime(result.HeartbeatTime.Value);
                clientInfo.UpdateLastHeartbeatTime(result.HeartbeatTime.Value);
            }

            HandleSuccessfulHeartbeat();
        }
        catch (HeartbeatFailedException ex)
        {
            // All retries exhausted - handle persistent failure
            HandlePersistentHeartbeatFailure(ex.Message);
        }
    }

    /// <summary>
    /// Handle successful heartbeat
    /// </summary>
    private void HandleSuccessfulHeartbeat()
    {
        // Reset consecutive failures on success
        _consecutiveFailures = 0;

        // Ensure registration status is completed
        if (RuntimeInfo.CurrentState != HostedServiceState.Running)
        {
            _registrationCompletionSource.TrySetResult(true);
        }

        RecordState("Heartbeat successful", HostedServiceState.Running);

        leaderService.SetRegistrationAvailable(true);
    }

    /// <summary>
    /// Handle persistent heartbeat failure (after all retries exhausted)
    /// </summary>
    private void HandlePersistentHeartbeatFailure(string? errorMessage)
    {
        _consecutiveFailures++;
        RecordState($"Heartbeat failed after retries: {errorMessage}, consecutive failures: {_consecutiveFailures}", HostedServiceState.Degraded);

        leaderService.SetRegistrationAvailable(false);

    }

    /// <summary>
    /// Internal exception for heartbeat failures
    /// </summary>
    private sealed class HeartbeatFailedException(string message) : Exception(message);
}
