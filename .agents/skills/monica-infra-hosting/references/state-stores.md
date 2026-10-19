# State stores

Inside the host's `builder.AddMonica(monica => { ... })` callback, `monica.AddStateStore()` registers `IMemoryStateStore` and makes it the default unkeyed `IStateStore`. It is process-local. Consumers inject `IStateStore` and use `GetStateAsync<T>(key)`, `SaveStateAsync(key, value, cancellationToken, ttl)`, `DeleteStateAsync(key)`, and ETag-based methods when concurrent updates matter. `GetStateAsync<T>` returns `default(T)` for a missing key; use `ExistAsync(key)` when the distinction matters for value types. Provider capabilities such as query and key scanning vary, so check the chosen provider before depending on them.

For the concrete Memory provider, omit the TTL or pass `null` for a permanent entry. Any supplied duration is added to the current time; `TimeSpan.Zero` therefore expires immediately. Do not assume that a zero-duration convention applies uniformly across providers. Memory's inherited `ExistAsync` tests a non-null object read: an actual null payload reports false, and its typed value/ETag read returns null with an empty ETag, although its key can remain in the scan registry. A JSON-null element is a distinct stored value. Use the actual existence, value, scan and TTL contracts for the chosen provider rather than treating those observations as interchangeable.

For a shared default, choose a distributed provider on the `AddStateStore()` registration. `SetCommonDistributedStateStoreProvider<TProvider>()` accepts a custom `IDistributedStateStore`; the Redis and Dapr extensions include their provider modules and setup:

```csharp
builder.AddMonica(monica =>
{
    monica.AddStateStore()
        .UseRedisStateStoreProvider(options =>
        {
            options.UseNormalConnection("redis", 6379);
            options.KeyPrefix = "my-app";
        });
});
```

`UseDaprStateStoreProvider(options => options.StateStoreName = "app-state")` instead uses the Dapr sidecar and requires a state-store component with the same name. Dapr's client module brings in sidecar readiness monitoring. Redis also supports Sentinel and cluster connection methods. Configure credentials and endpoints from host configuration. Typed distributed values follow the host's canonical JSON contract; changing that contract can make persisted values unreadable.

Redis appends `:` between a nonempty `KeyPrefix` and each logical key; `my-app` therefore stores `my-app:<key>`. The provider does not add an application ID. Use the same connection, database, prefix and JSON contract for every participant that shares state, and an independent namespace for another environment or persistence format. Redis hashes use `data` and `version` fields; selecting this provider does not migrate existing Dapr state.

Redis `DefaultTTL` defaults to `null` (permanent). An omitted per-write TTL uses that default; an explicit `TimeSpan.Zero` makes the new value permanent. Positive durations round up to whole milliseconds, and a successful write replaces the previous expiry, including removing it for a permanent value. Negative durations are rejected. TTL applies to normal, bulk, conditional and create-if-absent writes. Bulk operations pipeline independent keys and do not form a transaction.

Redis existence tests the stored `data` field, so a JSON `null` payload exists even though its typed value is null. Missing reads return default values and an empty ETag. Conditional writes and deletes require an existing matching ETag; empty or `"0"` ETags are not wildcard writes. Use `TrySaveStateIfNotExistsAsync` for atomic creation and the unconditional methods for deliberate overwrites or deletions. Wrong Redis types, authentication and network failures remain errors; incrementing an invalid integer version fails without replacing the payload. Scans enumerate connected primary nodes and return logical keys within the configured prefix; they are not a consistent snapshot. Redis does not implement `QueryStateAsync`.

Connections created by module registration are host-owned singletons. `RedisConnectionConfiguration` supports data-node ACL `User`/`Password`, TLS `Ssl`/`SslHost`, timeouts and additional endpoints. Sentinel's optional `SentinelUser`/`SentinelPassword` independently authenticate its control connection; null selects the default user without a password. The `UseSentinelConnection(..., password: ...)` convenience method explicitly uses that password for both Sentinel and data nodes; set `Connection` properties for independent credentials. Keep the connection factory alive until its data connections are disposed: it owns Sentinel monitoring connections and releases them at host shutdown. If constructing the factory manually, dispose it after all returned connections. Cluster mode requires database zero.

Single-key Dapr reads treat the Redis `nil` response for the requested key and store as missing state, including when the sidecar reports it as an `Internal` gRPC failure. Typed reads return `default(T)`, raw reads return `null`, and ETag reads return an empty ETag. Conditional creation can then proceed through its normal missing-key path. Other failures, including connection, authentication, wrong-type, and cancellation errors, remain failures; write failures are never converted into missing state.

Use `AddKeyedCommonStateStore(key, useDistributed: true)` to expose the selected common provider under a key, or `AddKeyedStateStore<TProvider>(key)`, `AddKeyedRedisStateStore(key, configureOptions)`, or `AddKeyedDaprStateStore(key, configureOptions)` for independently configured instances. Resolve with `GetRequiredKeyedService<IStateStore>(key)`. A keyed Redis/Dapr store does not automatically replace the unkeyed default. Selecting a distributed common store declares and satisfies the `distributed-provider` feature; requesting distributed storage without a provider fails composition rather than silently falling back to memory.

`AddStateStoreUI()` adds an optional shell page at `/state-store-dashboard` for inspecting state. It requires the StateStore module, localization, and the shell. Its browser adapters cover memory, Redis, and Dapr; the selected provider still determines which operations, such as key scanning or query, can work. `MaxKeysPerPage` defaults to 50 displayed results. A scan result's matched count and `HasMore` describe a truncated snapshot; the adapter does not supply a cursor or a next page, and its scan can enumerate more keys than the display limit. Exact lookup distinguishes an existing record from a successful missing result. TTL and ETag are observed metadata, not an atomic snapshot or a live countdown.

Create, edit and delete default to enabled and have independent UI option gates. Creation uses create-if-absent; an ETag, when supplied, protects a conditional update or single deletion. Bulk deletion names explicit selected keys without per-key ETags; it does not mean all matches or all provider keys. The dialog's zero-second TTL input is converted to a null duration before persistence. These UI controls do not configure host authorization. For service-discovery storage, read [service-discovery.md](service-discovery.md): its choice binds directly to the selected memory/distributed/keyed service, independently of the default unkeyed `IStateStore`.

Check `Monica.StateStore/Modules/ModuleStateStore.cs`, `Monica.StateStore.StackExchange/Modules/ModuleRedisStateStore.cs`, and `Monica.Dapr/Modules/ModuleDaprStateStore.cs` for provider registration, and `Monica.StateStore.UI/Modules/ModuleStateStoreUI.cs` for the optional browser.
