namespace Monica.Locker.Leases.Diagnostics;

/// <summary>Describes one explicitly permitted inventory query against a host-owned keyed lease store.</summary>
/// <remarks>The provider kind is declared by the host; it is descriptive and never grants administration.</remarks>
public sealed record LeaseAuthority
{
    /// <summary>Creates an authority binding. The store key must resolve a singleton IExclusiveLeaseStore.</summary>
    /// <param name="id">Stable, unique UI identity, independent of the display name.</param>
    /// <param name="displayName">Operator-facing host label; never a connection string.</param>
    /// <param name="storeKey">Existing keyed singleton store registration.</param>
    /// <param name="scope">Exact permitted deployment namespace.</param>
    /// <param name="namePrefix">Permitted ordinal resource prefix; empty includes all roles in the scope.</param>
    /// <param name="kind">Declared storage semantics shown to operators.</param>
    /// <param name="allowRevocation">Explicit administration opt-in, false by default; capability and authorization are also required.</param>
    public LeaseAuthority(string id, string displayName, string storeKey, string scope, string namePrefix,
        LeaseAuthorityKind kind, bool allowRevocation = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);
        _ = new LeaseResource(scope, "validation");
        ArgumentNullException.ThrowIfNull(namePrefix);
        if (namePrefix.Length > 512) throw new ArgumentOutOfRangeException(nameof(namePrefix));
        Id = id; DisplayName = displayName; StoreKey = storeKey; Scope = scope;
        NamePrefix = namePrefix; Kind = kind; AllowRevocation = allowRevocation;
    }

    /// <summary>Gets the stable binding identifier used in deep links.</summary>
    public string Id { get; }
    /// <summary>Gets the host-provided display label.</summary>
    public string DisplayName { get; }
    /// <summary>Gets the singleton store service key.</summary>
    public string StoreKey { get; }
    /// <summary>Gets the exact permitted resource scope.</summary>
    public string Scope { get; }
    /// <summary>Gets the permitted ordinal name prefix.</summary>
    public string NamePrefix { get; }
    /// <summary>Gets the host-declared provider semantics.</summary>
    public LeaseAuthorityKind Kind { get; }
    /// <summary>Gets whether this binding allows administrative revocation when the provider and caller also permit it.</summary>
    public bool AllowRevocation { get; }

    internal bool Includes(LeaseResource resource) => resource.Scope == Scope
        && resource.Name.StartsWith(NamePrefix, StringComparison.Ordinal);
}

/// <summary>Identifies the semantics of an explicitly configured lease source, not a runtime health state.</summary>
public enum LeaseAuthorityKind
{
    /// <summary>Relational authority; transactional fencing still depends on the consumer.</summary>
    Sql,
    /// <summary>Redis advisory authority; asynchronous failover may lose acknowledged grants.</summary>
    Redis,
    /// <summary>One process only, without cross-host coordination.</summary>
    InMemory,
    /// <summary>Host-specific authority whose guarantees must be established by its implementation.</summary>
    Custom
}

/// <summary>Contains one authority's retained evidence and the outcome of its most recent inventory attempt.</summary>
/// <param name="Authority">Explicit query and provider identity.</param>
/// <param name="Snapshot">Last successful source-clock observation, or null when none exists.</param>
/// <param name="RefreshFailed">Whether the most recent read failed; a previous snapshot retains its original time.</param>
/// <param name="CanRevoke">Binding and provider administration capability; caller authorization is checked again on mutation.</param>
/// <param name="TotalCount">Records returned before the presentation limit.</param>
public sealed record LeaseAuthorityEvidence(LeaseAuthority Authority, LeaseSnapshot? Snapshot,
    bool RefreshFailed, bool CanRevoke, int TotalCount)
{
    /// <summary>Gets whether the retained presentation was bounded; this does not bound the provider's scan.</summary>
    public bool IsTruncated => TotalCount > (Snapshot?.Leases.Count ?? 0);
}

/// <summary>Returns exact-resource evidence without replacing an authority's cached inventory.</summary>
/// <param name="Authority">The configured binding that contains the requested resource.</param>
/// <param name="Snapshot">The source observation time and zero or one retained grant; absence is not history.</param>
/// <param name="CanRevoke">The binding and provider permit revocation; current caller access is checked on execution.</param>
public sealed record LeaseResourceEvidence(LeaseAuthority Authority, LeaseSnapshot Snapshot, bool CanRevoke);
