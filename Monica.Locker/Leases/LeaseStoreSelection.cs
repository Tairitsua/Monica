namespace Monica.Locker.Leases;

/// <summary>Selects a host-owned keyed lease store and the shared authority namespace used by its participants.</summary>
/// <remarks>
/// Resolve the key once at composition. Use the same scope for every replica protecting the same data, and different
/// scopes for independent databases/environments sharing a backend. Neither the process ID nor a connection string
/// is a suitable scope. Selecting an external store does not add transaction fencing to another database.
/// </remarks>
public sealed record LeaseStoreSelection
{
    /// <summary>Creates an explicit binding; both values must be nonblank and scope is limited to 256 characters.</summary>
    public LeaseStoreSelection(string serviceKey, string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (scope.Length > 256) throw new ArgumentOutOfRangeException(nameof(scope));
        ServiceKey = serviceKey;
        Scope = scope;
    }

    /// <summary>The singleton <see cref="IExclusiveLeaseStore"/> registration key.</summary>
    public string ServiceKey { get; }
    /// <summary>The stable authority namespace shared by the participating hosts.</summary>
    public string Scope { get; }
}
