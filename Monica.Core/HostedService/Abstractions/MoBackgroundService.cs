using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Abstractions.Internal;
using Monica.Core.HostedService.Models;
using Monica.Core.HostedService.Services.Support;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Core.ObservableInstance.Abstractions.Internal;
using Monica.Core.ObservableInstance.Models;
using Monica.Modules;
using Monica.Tool.Extensions;

namespace Monica.Core.HostedService.Abstractions;

/// <summary>
/// Base class for observable BackgroundService implementations with built-in state management,
/// exception tracking, and heartbeat monitoring.
/// </summary>
public abstract class MoBackgroundService : BackgroundService, IMoHostedService, IHostedServiceRuntimeOwner
{
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ModuleHostedServiceOption _options;
    private readonly IObservableInstanceRegistry _observableManager;
    private HostedServiceRuntimeInfo? _runtimeInfo;

    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;

    /// <summary>
    /// Initializes an observable background service with dependencies owned by the current host.
    /// </summary>
    /// <param name="observableManager">The registry used to expose service state.</param>
    /// <param name="options">The shared hosted-service options.</param>
    /// <param name="serviceScopeFactory">Creates operation scopes for lifecycle and finite work-item behaviors.</param>
    /// <param name="logger">The logger for the concrete background service.</param>
    protected MoBackgroundService(
        IObservableInstanceRegistry observableManager,
        IOptions<ModuleHostedServiceOption> options,
        IServiceScopeFactory serviceScopeFactory,
        ILogger logger)
    {
        _observableManager = observableManager;
        _options = options.Value;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected ILogger Logger => _logger;

    /// <summary>
    /// Gets the name of the service for identification purposes
    /// </summary>
    public virtual string ServiceName => GetType().Name;

    /// <summary>
    /// Gets the optional key that distinguishes this hosted-service instance from other instances of the same type.
    /// </summary>
    public virtual string? ServiceKey => null;

    /// <summary>
    /// Gets the observable group identifier used to group related hosted services.
    /// Return null to leave the service ungrouped.
    /// </summary>
    public virtual string? ServiceGroupId => null;

    /// <summary>
    /// Gets the maximum number of state history entries to retain
    /// </summary>
    public virtual int MaxHistorySize => _options.DefaultMaxHistorySize;

    /// <summary>
    /// Gets the heartbeat interval for this service (null to disable heartbeat)
    /// </summary>
    public virtual TimeSpan? HeartbeatInterval => _options.DefaultHeartbeatInterval;

    /// <summary>
    /// Gets the runtime information for this service.
    /// </summary>
    public HostedServiceRuntimeInfo RuntimeInfo => _runtimeInfo ??= CreateRuntimeInfo();

    /// <summary>
    /// Creates runtime information lazily after the derived hosted service has finished construction.
    /// </summary>
    private HostedServiceRuntimeInfo CreateRuntimeInfo()
    {
        var trackerId = $"HostedService_{ServiceName}_{Guid.NewGuid():N}";
        var tracker = _observableManager.Register(trackerId, opt =>
        {
            opt.MaxHistorySize = MaxHistorySize;
            opt.InstanceName = ServiceName;
            opt.InstanceType = GetType();
            opt.InstanceKey = ServiceKey;
            opt.GroupId = ServiceGroupId;
            opt.Logger = Logger;
        });

        try
        {
            ConfigureStateLogLevels(tracker);

            return new HostedServiceRuntimeInfo(tracker)
            {
                HeartbeatInterval = HeartbeatInterval
            };
        }
        catch
        {
            ReleaseTracker(tracker);
            throw;
        }
    }

    void IHostedServiceRuntimeOwner.ReleaseRuntimeInfo()
    {
        var runtimeInfo = Interlocked.Exchange(ref _runtimeInfo, null);
        if (runtimeInfo is not null)
        {
            ReleaseTracker(runtimeInfo.Tracker);
        }
    }

    private void ReleaseTracker(ObservableInstanceTracker tracker)
    {
        if (_observableManager is not IObservableInstanceRegistryWriter writer)
        {
            throw new InvalidOperationException(
                $"Observable registry '{_observableManager.GetType().FullName}' does not support reversible registrations.");
        }

        _ = writer.Unregister(tracker.InstanceId);
    }

    /// <summary>
    /// Configures default log level mappings for HostedServiceState.
    /// Override to customize per service.
    /// </summary>
    protected virtual void ConfigureStateLogLevels(ObservableInstanceTracker tracker)
    {
        tracker.SetDebugStates(HostedServiceState.NotStarted, HostedServiceState.Starting);
        tracker.SetInformationStates(
            HostedServiceState.Running,
            HostedServiceState.Executing,
            HostedServiceState.WaitingDependency,
            HostedServiceState.Stopping,
            HostedServiceState.Stopped
        );
        tracker.SetWarningStates(HostedServiceState.Degraded);
        tracker.SetErrorStates(HostedServiceState.Faulted);
    }

    /// <inheritdoc cref="ObservableInstanceTracker.RecordState" />
    protected void RecordState(string message,
        HostedServiceState? newState = null,
        Exception? exception = null,
        LogLevel? logLevel = null)
    {
        RuntimeInfo.Tracker.RecordState(message, newState, exception, logLevel);
    }

    /// <summary>
    /// Starts the heartbeat monitoring task
    /// </summary>
    private void StartHeartbeat()
    {
        if (HeartbeatInterval == null || HeartbeatInterval.Value <= TimeSpan.Zero)
            return;

        _heartbeatCts = new CancellationTokenSource();
        var heartbeatToken = _heartbeatCts.Token;
        // Let the loop observe cancellation even when shutdown precedes its scheduled execution.
        // Capture the token so queued execution does not access the source after disposal.
        _heartbeatTask = Task.Run(async () =>
        {
            while (!heartbeatToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HeartbeatInterval.Value, heartbeatToken);
                    RuntimeInfo.LastHeartbeat = DateTime.UtcNow;
                    await OnHeartbeatAsync(heartbeatToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "{ServiceName} heartbeat error", ServiceName);
                }
            }
        });
    }

    /// <summary>
    /// Called on each heartbeat tick (override for custom heartbeat logic)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    protected virtual Task OnHeartbeatAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Starts the background service through the shared execution pipeline.
    /// Derived services customize startup through <see cref="OnStartingAsync"/> and
    /// <see cref="OnStartedAsync"/> so their work remains inside the lifecycle boundary.
    /// </summary>
    /// <param name="cancellationToken">Signals that application startup is being aborted.</param>
    public sealed override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await HostedServiceExecutionAdapter.ExecuteLifecycleAsync(
                _serviceScopeFactory,
                this,
                ServiceName,
                HostedServiceExecutionPoints.Start,
                HostedServiceLifecyclePhase.Start,
                async () =>
                {
                    RecordState("Service starting", HostedServiceState.Starting);
                    RuntimeInfo.StartedAt = DateTime.UtcNow;

                    await OnStartingAsync(cancellationToken).ConfigureAwait(false);
                    await base.StartAsync(cancellationToken).ConfigureAwait(false);
                    StartHeartbeat();

                    RecordState("Service started, executing background work", HostedServiceState.Running);
                    await OnStartedAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordState("Service start failed", HostedServiceState.Faulted, ex);

            if (_options.FailFastOnStartupError)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Stops the background service through the shared execution pipeline.
    /// Derived services customize shutdown through <see cref="OnStoppingAsync"/> and
    /// <see cref="OnStoppedAsync"/> so all cleanup remains inside the lifecycle boundary.
    /// </summary>
    /// <param name="cancellationToken">Signals the graceful-shutdown deadline.</param>
    public sealed override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await HostedServiceExecutionAdapter.ExecuteLifecycleAsync(
                _serviceScopeFactory,
                this,
                ServiceName,
                HostedServiceExecutionPoints.Stop,
                HostedServiceLifecyclePhase.Stop,
                async () =>
                {
                    RecordState("Service stopping", HostedServiceState.Stopping);
                    await OnStoppingAsync(cancellationToken).ConfigureAwait(false);

                    if (_heartbeatCts != null)
                    {
                        try { await _heartbeatCts.CancelAsync().ConfigureAwait(false); }
                        catch (ObjectDisposedException) { }
                    }
                    if (_heartbeatTask != null)
                    {
                        await _heartbeatTask.ConfigureAwait(false);
                    }
                    _heartbeatCts.SafeCancelAndDispose();
                    _heartbeatCts = null;

                    await base.StopAsync(cancellationToken).ConfigureAwait(false);

                    RuntimeInfo.StoppedAt = DateTime.UtcNow;
                    RecordState("Service stopped", HostedServiceState.Stopped);
                    await OnStoppedAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordState("Service stop failed", HostedServiceState.Faulted, ex);
        }
    }

    /// <summary>
    /// Performs derived startup work before the permanent background operation starts.
    /// The hook executes inside the hosted-service start pipeline.
    /// </summary>
    /// <param name="cancellationToken">Signals that application startup is being aborted.</param>
    protected virtual Task OnStartingAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Performs derived startup work after the permanent background operation has started.
    /// The hook executes before the hosted-service start pipeline completes.
    /// </summary>
    /// <param name="cancellationToken">Signals that application startup is being aborted.</param>
    protected virtual Task OnStartedAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Performs derived shutdown work before the permanent background operation is stopped.
    /// The hook executes inside the hosted-service stop pipeline.
    /// </summary>
    /// <param name="cancellationToken">Signals the graceful-shutdown deadline.</param>
    protected virtual Task OnStoppingAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Performs derived shutdown work after the permanent background operation has stopped.
    /// The hook executes before the hosted-service stop pipeline completes.
    /// </summary>
    /// <param name="cancellationToken">Signals the graceful-shutdown deadline.</param>
    protected virtual Task OnStoppedAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Wraps ExecuteAsync to track state and handle errors
    /// </summary>
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            RecordState("Executing background work", HostedServiceState.Executing);
            await ExecuteBackgroundAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("{ServiceName} background work cancelled", ServiceName);
        }
        catch (Exception ex)
        {
            RecordState("Background work failed", HostedServiceState.Faulted, ex);
        }
    }

    /// <summary>
    /// Executes the background work for this service (override in derived classes)
    /// </summary>
    /// <param name="stoppingToken">Triggered when the application host is performing a graceful shutdown</param>
    protected abstract Task ExecuteBackgroundAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Resolves and executes one finite scoped work item through the shared execution pipeline.
    /// The permanent background loop itself is intentionally never wrapped in business behaviors.
    /// </summary>
    /// <typeparam name="TWorkItem">The scoped work-item implementation.</typeparam>
    /// <param name="cancellationToken">Signals that the hosted service is stopping.</param>
    protected Task ExecuteWorkItemAsync<TWorkItem>(CancellationToken cancellationToken)
        where TWorkItem : class, IHostedServiceWorkItem
    {
        return HostedServiceExecutionAdapter.ExecuteWorkItemAsync<TWorkItem>(
            _serviceScopeFactory,
            this,
            ServiceName,
            cancellationToken);
    }

    /// <summary>
    /// Resolves and executes one finite typed scoped work item through the shared execution pipeline.
    /// The permanent background loop itself is intentionally never wrapped in business behaviors.
    /// </summary>
    /// <typeparam name="TWorkItem">The scoped work-item implementation.</typeparam>
    /// <typeparam name="TInput">The work-item input type.</typeparam>
    /// <typeparam name="TResult">The work-item result type.</typeparam>
    /// <param name="input">The work-item input.</param>
    /// <param name="cancellationToken">Signals that the hosted service is stopping.</param>
    protected Task<TResult> ExecuteWorkItemAsync<TWorkItem, TInput, TResult>(
        TInput input,
        CancellationToken cancellationToken)
        where TWorkItem : class, IHostedServiceWorkItem<TInput, TResult>
    {
        return HostedServiceExecutionAdapter.ExecuteWorkItemAsync<TWorkItem, TInput, TResult>(
            _serviceScopeFactory,
            this,
            ServiceName,
            input,
            cancellationToken);
    }

    /// <summary>
    /// Disposes resources
    /// </summary>
    public override void Dispose()
    {
        _heartbeatCts.SafeCancelAndDispose();
        _heartbeatCts = null;
        base.Dispose();
    }
}
