# Monica Hosted-Service Development

Use `MoBackgroundService` for long-lived host work that needs Monica state history, exception tracking, heartbeat observation, and execution-pipeline integration.

Source locations:

- `Monica.Core/HostedService/Abstractions/MoBackgroundService.cs`
- `Monica.ServiceDiscovery/Services/Support/CoordinatedLeaderService.cs`

## `MoBackgroundService`

### Required constructor dependencies

```csharp
public sealed class MyMonitorService(
    IObservableInstanceRegistry observableRegistry,
    IOptions<ModuleHostedServiceOption> hostedServiceOptions,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<MyMonitorService> logger,
    IMyDependency dependency)
    : MoBackgroundService(
        observableRegistry,
        hostedServiceOptions,
        serviceScopeFactory,
        logger)
{
    protected override async Task ExecuteBackgroundAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dependency.RunAsync(stoppingToken);
                RecordState("Monitor cycle completed", logLevel: LogLevel.Information);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                RecordState(
                    "Monitor cycle failed",
                    HostedServiceState.Degraded,
                    exception,
                    LogLevel.Error);
            }
        }
    }
}
```

| Dependency | Purpose |
|---|---|
| `IObservableInstanceRegistry` | Registers the service's host-owned `ObservableInstanceTracker` and supports diagnostics queries. |
| `IOptions<ModuleHostedServiceOption>` | Supplies history, heartbeat, and startup-failure defaults. |
| `IServiceScopeFactory` | Creates scopes for lifecycle behaviors and finite work items. |
| `ILogger<TService>` | Powers state-aware logging and heartbeat diagnostics. |

`ServiceName` defaults to the concrete type name. Override `ServiceName`, `ServiceKey`, or `ServiceGroupId` only when the runtime identity needs an explicit stable distinction.

### State and lifecycle contract

Use `RecordState(...)` for operational state so one action updates the observable tracker and emits the intended log. Pass an exception when it must appear in diagnostics.

The built-in states are `NotStarted`, `Starting`, `Running`, `Executing`, `WaitingDependency`, `Stopping`, `Stopped`, `Degraded`, and `Faulted`. Override `ConfigureStateLogLevels(ObservableInstanceTracker tracker)` only when the default mapping is unsuitable.

The base class seals `StartAsync`, `StopAsync`, and `ExecuteAsync`. Customize lifecycle work through:

- `OnStartingAsync` and `OnStartedAsync`
- `ExecuteBackgroundAsync`
- `OnStoppingAsync` and `OnStoppedAsync`
- `OnHeartbeatAsync` when `HeartbeatInterval` is enabled

Heartbeat shutdown is cooperative inside its worker loop. Stopping before that worker begins must still drain it,
complete background cleanup, and run the remaining stop hooks inside the lifecycle pipeline. Await `StopAsync`
before disposing the service.

Do not create an unmanaged scope around the permanent background loop. For finite business work that should run through Monica's execution pipeline, register an `IHostedServiceWorkItem` and invoke `ExecuteWorkItemAsync<TWorkItem>(...)` or its typed input/result overload.

Register the service normally:

```csharp
public override void ConfigureServices(ModuleContext<ModuleExampleOption> context)
{
    context.Services.AddHostedService<MyMonitorService>();
}
```

## Observable-instance queries

Inject `IObservableInstanceRegistry` into host-internal services that need tracker queries:

```csharp
var allInstances = observableRegistry.GetAllInstances();
var faultedInstances = observableRegistry.GetInstancesWithExceptions();
var runningServices = observableRegistry.GetInstancesByState(HostedServiceState.Running);
```

UI or external host consumers should use `ObservableInstanceFacade` rather than depending on the registry implementation.

## `CoordinatedLeaderService`

Use `CoordinatedLeaderService` when work may run on only the elected instance. It belongs to `Monica.ServiceDiscovery`, waits for service registration unless `SkipRegistrationWait` is configured, subscribes to leader transitions, and cancels leader-scoped work when leadership is lost.

```csharp
public sealed class MyCleanupService(
    ILeaderElectionService leaderService,
    IOptions<ModuleServiceDiscoveryOption> serviceDiscoveryOptions,
    IServiceRegistrationCoordinator registrationCoordinator,
    IObservableInstanceRegistry observableRegistry,
    IOptions<ModuleHostedServiceOption> hostedServiceOptions,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<MyCleanupService> logger,
    IMyRepository repository)
    : CoordinatedLeaderService(
        leaderService,
        serviceDiscoveryOptions,
        registrationCoordinator,
        observableRegistry,
        hostedServiceOptions,
        serviceScopeFactory,
        logger)
{
    protected override Task OnBecameLeaderAsync(CancellationToken leaderToken)
    {
        RecordState("Cleanup service became leader", logLevel: LogLevel.Information);
        return Task.CompletedTask;
    }

    protected override async Task LeaderExecuteBackgroundAsync(CancellationToken leaderToken)
    {
        while (!leaderToken.IsCancellationRequested)
        {
            await repository.DeleteExpiredAsync(leaderToken);
            await Task.Delay(TimeSpan.FromHours(1), leaderToken);
        }
    }

    protected override Task OnLeaderLostAsync(LeaderLostReason reason)
    {
        RecordState($"Cleanup service lost leadership: {reason}", logLevel: LogLevel.Information);
        return Task.CompletedTask;
    }
}
```

`OnBecameLeaderAsync` is required and may execute more than once during one host lifetime. Make it idempotent or reset leader-owned state explicitly. `LeaderExecuteBackgroundAsync` and `OnLeaderLostAsync` are optional hooks; always honor the leader token.

## Checklist

- Use `RecordState` for observable operational messages and state changes.
- Catch cancellation only when the supplied stopping or leader token was cancelled.
- Keep the permanent loop responsive to cancellation.
- Use `ExecuteWorkItemAsync` for finite scoped business operations.
- Make leader initialization repeatable and clean up leader-owned resources on loss.
- Prefer `IObservableInstanceRegistry` internally and the Facade at UI/API boundaries.
