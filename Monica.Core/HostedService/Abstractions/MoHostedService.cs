using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Execution;
using Monica.Core.HostedService.Abstractions.Internal;
using Monica.Core.HostedService.Models;
using Monica.Core.HostedService.Services.Support;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Core.ObservableInstance.Abstractions.Internal;
using Monica.Core.ObservableInstance.Models;
using Monica.Modules;

namespace Monica.Core.HostedService.Abstractions;

/// <summary>
/// Base class for observable <see cref="IHostedService"/> implementations with state and exception tracking.
/// </summary>
public abstract class MoHostedService : IHostedService, IMoHostedService, IHostedServiceRuntimeOwner
{
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ModuleHostedServiceOption _options;
    private readonly IObservableInstanceRegistry _observableManager;
    private HostedServiceRuntimeInfo? _runtimeInfo;

    /// <summary>
    /// Initializes an observable hosted service with dependencies owned by the current host.
    /// </summary>
    /// <param name="observableManager">The registry used to expose service state.</param>
    /// <param name="options">The shared hosted-service options.</param>
    /// <param name="serviceScopeFactory">Creates operation scopes for lifecycle and finite work-item behaviors.</param>
    /// <param name="logger">The logger for the concrete hosted service.</param>
    protected MoHostedService(
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
    /// Gets the heartbeat interval (always null for MoHostedService, only applicable to MoBackgroundService)
    /// </summary>
    public virtual TimeSpan? HeartbeatInterval => null;

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
        HostedServiceState? newState,
        Exception? exception = null,
        LogLevel? logLevel = null)
    {
        RuntimeInfo.Tracker.RecordState(message, newState, exception, logLevel);
    }

    /// <summary>
    /// Starts the hosted service through a non-overridable template that surrounds all derived startup hooks
    /// with the shared execution pipeline.
    /// </summary>
    /// <param name="cancellationToken">Signals that application startup is being aborted.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var role = RuntimeInfo.Role;
        if (role?.TryBeginStart() == false)
        {
            RecordState("Startup skipped because the owning role is quarantined", HostedServiceState.Faulted);
            return;
        }
        try
        {
            await ExecuteLifecycleAsync(
                HostedServiceExecutionPoints.Start,
                HostedServiceLifecyclePhase.Start,
                async () =>
                {
                    RecordState("Service starting", HostedServiceState.Starting);
                    RuntimeInfo.StartedAt = DateTime.UtcNow;

                    await OnStartingAsync(cancellationToken).ConfigureAwait(false);

                    if (!RuntimeInfo.Tracker.IsUnhealthy())
                    {
                        RecordState("Service started successfully", HostedServiceState.Running);
                    }

                    await OnStartedAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordState("Service start failed", HostedServiceState.Faulted, ex);
            if (role is null)
            {
                if (_options.FailFastOnStartupError) throw;
                return;
            }

            if (!role.TryIsolateStartupFault()) throw;
        }
        finally
        {
            role?.CompleteStart();
        }
    }

    /// <summary>
    /// Stops the hosted service through a non-overridable template that surrounds all derived shutdown hooks
    /// with the shared execution pipeline.
    /// </summary>
    /// <param name="cancellationToken">Signals the graceful-shutdown deadline.</param>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        var role = RuntimeInfo.Role;
        if (role?.Skipped == true) return Task.CompletedTask;
        return role is null ? StopCoreAsync(cancellationToken) : role.StopOnceAsync(() => StopCoreAsync(cancellationToken));
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteLifecycleAsync(
                HostedServiceExecutionPoints.Stop,
                HostedServiceLifecyclePhase.Stop,
                async () =>
                {
                    RecordState("Service stopping", HostedServiceState.Stopping);

                    await OnStoppingAsync(cancellationToken).ConfigureAwait(false);

                    RuntimeInfo.StoppedAt = DateTime.UtcNow;
                    RecordState("Service stopped successfully", HostedServiceState.Stopped);

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
    /// Called during service startup, before the service is marked as running.
    /// The hook executes inside the hosted-service start pipeline.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    protected virtual Task OnStartingAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Called after the service has started successfully and before the hosted-service start pipeline completes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    protected virtual Task OnStartedAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Called during service shutdown, before the service is marked as stopped.
    /// The hook executes inside the hosted-service stop pipeline.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    protected virtual Task OnStoppingAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Called after the service has stopped successfully and before the hosted-service stop pipeline completes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    protected virtual Task OnStoppedAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Resolves and executes one finite scoped work item through the shared execution pipeline.
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

    private async Task ExecuteLifecycleAsync(
        ExecutionPoint point,
        HostedServiceLifecyclePhase phase,
        Func<Task> terminal,
        CancellationToken cancellationToken)
    {
        await HostedServiceExecutionAdapter.ExecuteLifecycleAsync(
            _serviceScopeFactory,
            this,
            ServiceName,
            point,
            phase,
            terminal,
            cancellationToken)
            .ConfigureAwait(false);
    }
}
