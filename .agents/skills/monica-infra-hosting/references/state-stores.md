# State stores

Inside the host's `builder.AddMonica(monica => { ... })` callback, `monica.AddStateStore()` registers `IMemoryStateStore` and makes it the default unkeyed `IStateStore`. It is process-local. Consumers inject `IStateStore` and use `GetStateAsync<T>(key)`, `SaveStateAsync(key, value, cancellationToken, ttl)`, `DeleteStateAsync(key)`, and ETag-based methods when concurrent updates matter. `GetStateAsync<T>` returns `default(T)` for a missing key; use `ExistAsync(key)` when the distinction matters for value types. A zero TTL means permanent storage; provider capabilities such as query and key scanning vary, so check the chosen provider before depending on them.

For a shared default, choose a distributed provider on the `AddStateStore()` registration. `SetCommonDistributedStateStoreProvider<TProvider>()` accepts a custom `IDistributedStateStore`; the Redis and Dapr extensions include their provider modules and setup:

```csharp
builder.AddMonica(monica =>
{
    monica.AddStateStore()
        .UseRedisStateStoreProvider(options =>
        {
            options.UseNormalConnection("redis", 6379);
            options.KeyPrefix = "my-app:";
        });
});
```

`UseDaprStateStoreProvider(options => options.StateStoreName = "app-state")` instead uses the Dapr sidecar and requires a state-store component with the same name. Dapr's client module brings in sidecar readiness monitoring. Redis also supports Sentinel and cluster connection methods. Configure credentials and endpoints from host configuration. Typed distributed values follow the host's canonical JSON contract; changing that contract can make persisted values unreadable.

Use `AddKeyedCommonStateStore(key, useDistributed: true)` to expose the selected common provider under a key, or `AddKeyedStateStore<TProvider>(key)`, `AddKeyedRedisStateStore(key, configureOptions)`, or `AddKeyedDaprStateStore(key, configureOptions)` for independently configured instances. Resolve with `GetRequiredKeyedService<IStateStore>(key)`. A keyed Redis/Dapr store does not automatically replace the unkeyed default. Selecting a distributed common store declares and satisfies the `distributed-provider` feature; requesting distributed storage without a provider fails composition rather than silently falling back to memory.

`AddStateStoreUI()` adds an optional shell page for inspecting state. It requires the StateStore module, localization, and the shell. Its browser adapters cover memory, Redis, and Dapr; the selected provider still determines which operations, such as key scanning or query, can work. The UI defaults to at most 50 keys per page and allows create, edit, and delete, so choose its exposure and editing options deliberately for the host. For service-discovery storage, read [service-discovery.md](service-discovery.md): its choice binds directly to the selected memory/distributed/keyed service, independently of the default unkeyed `IStateStore`.

Check `Monica.StateStore/Modules/ModuleStateStore.cs`, `Monica.StateStore.StackExchange/Modules/ModuleRedisStateStore.cs`, and `Monica.Dapr/Modules/ModuleDaprStateStore.cs` for provider registration, and `Monica.StateStore.UI/Modules/ModuleStateStoreUI.cs` for the optional browser.
