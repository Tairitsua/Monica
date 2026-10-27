# Lock providers and ownership

For renewable database coordinator roles shared by JobScheduler and Outbox, use [coordinator leases](leases.md). The handle API described here retains its existing backend-specific acquisition, monitoring, and release behavior.

Register `monica.AddLocker()` inside the host's `builder.AddMonica(...)` callback and select a provider. A missing provider fails module composition. `UseInProcessProvider()` coordinates only callers in the same host. `UseMedallionProvider(provider)` adapts an existing Medallion provider instance; its caller owns that supplied instance. `UseDaprProvider(...)` uses a named Dapr lock-store component:

```csharp
using Monica.Core.Modularity.Extensions;
using Monica.Modules;

builder.AddMonica(monica =>
{
    monica.AddLocker(options => options.LockKeyPrefix = "shared-work:")
        .UseDaprProvider(options => options.StoreName = "shared-lock-store");
});
```

For a native Medallion backend, `UseMedallionProvider(Func<IServiceProvider, Medallion.Threading.IDistributedLockProvider>)` creates one provider from host services on first resolution. The container owns disposal of a disposable factory result. Register backend connections through their own singleton factories when the host should own them, and keep every dependency singleton-compatible. Do not capture a scoped repository or create a second service provider. The existing instance overload leaves provider and connection disposal with its caller. Both forms require acquired handles to be released before disposing their provider or backend connections. Native Redis connection construction can reuse the [Redis connection factory](state-stores.md) without selecting a StateStore provider; a separate lock multiplexer remains owned by its own registration.

The Locker service, selected provider adapter, and Dapr distributed lock client are host-owned singletons. Their dependencies must support singleton use. The Dapr module explicitly selects the SDK client's singleton lifetime so the singleton provider never captures the SDK's default scoped client. Keep scope validation enabled; an application does not need to register another client to repair this composition. The Dapr lock module uses its own client configuration and does not implicitly enable the separate Dapr client module's sidecar health monitoring.

Inject `Monica.Locker.Abstractions.IDistributedLock` and call `TryAcquireAsync(resourceName, options, cancellationToken)`. A successful result transfers ownership of the handle to the caller; release it with `await using`. Treat `null` as an acquisition that did not obtain a lock, and do not execute the protected operation. Providers can throw acquisition failures; do not assume every provider converts cancellation into `null`.

`ModuleLockerOption.LockKeyPrefix` defaults to an empty string and is concatenated directly with the logical resource name. Every participant protecting the same resource must use the same provider backend, store, prefix, and resource-name format. Process-local locks do not coordinate separate replicas. [Dapr locks are scoped to a Dapr app ID](https://docs.dapr.io/developing-applications/building-blocks/distributed-lock/distributed-lock-api-overview/); equal resource names in different app IDs do not establish a shared lock.

The native Medallion adapter does not add an app ID or process ID to the normalized key. Put deployment isolation in the host's prefix, and use the same prefix across replicas that protect the same resource. `LockAcquisitionOptions.Owner` does not change the resource key; Medallion and in-process providers ignore it.

## Lease constraints

`LockAcquisitionOptions.WaitTimeout` controls acquisition waiting; it does not limit the protected operation. When omitted, the Locker service applies `ModuleLockerOption.DefaultWaitTimeout`, which defaults to two minutes. The cancellation token supplied to `TryAcquireAsync` applies to acquisition; obtaining a handle does not automatically bind later work to that token.

`LeaseDuration` is provider-specific: the in-process and Medallion adapters do not apply it. Configure expiry and renewal through the selected Medallion provider's own options. Dapr's requested lease defaults to `ModuleDaprLockerOption.DefaultLeaseDuration` (two minutes), rounds up to whole seconds with a minimum of one second, and has a fixed expiry. Monica's Dapr adapter does not renew it. Disposing a handle releases the provider resource; it does not prove that ownership lasted until disposal.

`IDistributedLockHandle.HandleLostToken` reports loss of an acquired lock when the provider supports observation. The Medallion adapter forwards the exact native token; obtaining it can start backend monitoring. Read it while the handle is held. In-process and Dapr handles return `CancellationToken.None`: in-process semaphores have no independent lease expiry, while the Dapr adapter cannot observe its fixed lease's loss. A non-cancelable token does not guarantee continuing ownership.

For long operations, link caller cancellation with this signal and forward the combined token through protected work. This example accepts application-owned work as a callback; that callback must also observe the token before its durable commit or cursor update:

```csharp
using Monica.Locker.Abstractions;

static async Task ExecuteWithLockAsync(
    IDistributedLock locker,
    string resourceName,
    Func<CancellationToken, Task> execute,
    CancellationToken cancellationToken)
{
    await using var handle = await locker.TryAcquireAsync(
        resourceName, cancellationToken: cancellationToken);
    if (handle is null)
    {
        throw new TimeoutException("The protected resource is busy.");
    }

    using var operation = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken, handle.HandleLostToken);
    operation.Token.ThrowIfCancellationRequested();
    await execute(operation.Token);
    operation.Token.ThrowIfCancellationRequested();
}
```

The Medallion adapter propagates native acquisition cancellation as `OperationCanceledException`, backend failures, and release failures. Dapr's polling can return `null` on cancellation. Do not convert all acquisition exceptions into contention. Always await the protected operation and handle disposal, including after cancellation or lock loss; abandoning a still-running operation and releasing its handle permits overlapping work.

The Locker abstraction supplies no fencing or atomic transaction across a lock store and the application's database or remote service. An expired holder can still perform an unguarded mutation. For durable business state, retain database concurrency conditions and transaction ownership rather than treating a process lock or finite lease as the sole correctness boundary. For persistence operations, use the [persistence transaction contract](../../monica-infra-persistence/references/transactions.md).

For source and behavior evidence, inspect `Monica.Locker/Modules/ModuleLocker.cs`, `Monica.Locker/Abstractions/IDistributedLockHandle.cs`, the selected provider under `Monica.Locker/Providers`, and `Monica.Dapr/Modules/ModuleDaprLocker.cs`. Native token forwarding, cancellation, and release behavior are covered by `tests/Test.Monica.Locker/Providers/Medallion/MedallionLockProviderTests.cs`; host-owned factory and caller-owned instance behavior by `tests/Test.Monica.Locker/Modules/ModuleLockerCompositionTests.cs`. Dapr host composition and cross-scope singleton ownership are covered by `tests/Test.Monica.Dapr/Modules/ModuleDaprLockerCompositionTests.cs`.
