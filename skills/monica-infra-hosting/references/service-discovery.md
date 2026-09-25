# Service discovery

Select the role and storage mode inside the host's `AddMonica(...)` callback:

```csharp
builder.AddMonica(monica =>
{
    monica.AddServiceDiscovery()
        .AsStandalone()
        .UseMemoryStorage();
});
```

The role defaults to `Worker`: it registers and runs workloads without owning registry control-plane duties. `AsRegistry()` assigns registry duties; `AsStandalone()` combines registry and worker on one host. A storage mode is mandatory even for the default worker. `UseMemoryStorage()` is process-local and fits a single process; for multiple instances, select a shared store. `UseDistributedStorage()` requires a distributed provider selected on `ModuleStateStore`. `UseExternalKeyedStorage(key)` requires an existing keyed `IStateStore` registration under that exact key; there is no unkeyed fallback. Composition validates the storage choice and its service contract.

The module requires hosted services, localization, and a retry pipeline. Its `ServiceDiscoveryClientHostedService` owns registration and exposes `IServiceRegistrationCoordinator`; `ServiceDiscoveryFacade` provides scoped status queries. Optional registry-status and leader endpoints are mapped only on Web hosts when the Minimal API switch permits them, while worker services run on generic hosts. The role and storage choice are independent: changing a worker to a registry does not select a store. Identity options such as `AppId`, `DomainName`, and `AppName` fall back to `ConfigureApplication(...)` defaults; `IncludeListeningAddresses` defaults to `true`. `SkipRegistrationWait` defaults to `false`, and `RegistrationWaitTimeout` to five minutes, so inspect registration state when a dependent scheduler appears delayed.

After startup, inject `ServiceDiscoveryFacade` and call `GetServicesStatusAsync()` for registered service status, or `GetMergedServicesStatusAsync()` for the merged operational view. Use `GetDomainsAsync()` followed by `GetDomainDetailAsync(domainName)` to inspect a particular domain. Check each returned `Res<T>` before reading its data. If an expected instance is missing, compare its configured identity and selected storage with the querying host, then inspect registration readiness; a separate in-memory store cannot see another process. `GetRegistryLeaderStatusAsync()` helps distinguish missing registration from absent registry leadership. These queries inspect discovery state; use the messaging skill's [RPC reference](../../monica-infra-messaging/references/rpc.md) for making an application call.

For configuring the underlying distributed or keyed provider, read [state-stores.md](state-stores.md). Verify role, contracts, registration services, and defaults in `Monica.ServiceDiscovery/Modules/ModuleServiceDiscovery.cs`, and query contracts in `Monica.ServiceDiscovery/Facades/ServiceDiscoveryFacade.cs`.
