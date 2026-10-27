namespace Monica.Locker.Leases;

/// <summary>Names one exclusive role within a configured storage authority.</summary>
/// <remarks>Participants must use the same scope and name. Neither value identifies a process or a connection string.</remarks>
public sealed record LeaseResource
{
    /// <summary>Creates a bounded, case-sensitive resource key.</summary>
    public LeaseResource(string scope, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (scope.Length > 256) throw new ArgumentOutOfRangeException(nameof(scope));
        if (name.Length > 512) throw new ArgumentOutOfRangeException(nameof(name));
        Scope = scope;
        Name = name;
    }

    /// <summary>Gets the deployment or workload namespace, at most 256 characters.</summary>
    public string Scope { get; }
    /// <summary>Gets the module-qualified role name, at most 512 characters.</summary>
    public string Name { get; }
}

/// <summary>Identifies one acquisition; renewals retain its term and reacquisition always replaces it.</summary>
/// <remarks>A term is an opaque equality token, not an ordered fencing sequence. Validate it at the protected resource.</remarks>
public sealed record LeaseIdentity(LeaseResource Resource, string HolderId, string TermId);

/// <summary>Describes a confirmed ownership interval.</summary>
/// <remarks>Measure <see cref="ValidFor"/> from the caller's monotonic request start, never from response arrival.</remarks>
public sealed record LeaseGrant(LeaseIdentity Identity, TimeSpan ValidFor, DateTimeOffset ConfirmedAtUtc)
{
    /// <summary>Gets the authoritative expiry. The current SQL and memory providers supply their own clock.</summary>
    public DateTimeOffset ExpiresAtUtc => ConfirmedAtUtc.Add(ValidFor);
}

/// <summary>Captures current ownership evidence without exposing a usable term token.</summary>
public sealed record LeaseObservation(LeaseResource Resource, string HolderId,
    DateTimeOffset AcquiredAtUtc, DateTimeOffset ConfirmedAtUtc, DateTimeOffset ExpiresAtUtc);

/// <summary>Contains retained ownership records and the source clock sampled for their interpretation.</summary>
/// <remarks>This read-only evidence is not proof of process health or permission to mutate protected state.</remarks>
public sealed record LeaseSnapshot(DateTimeOffset ObservedAtUtc, IReadOnlyList<LeaseObservation> Leases);

/// <summary>Signals that an expected ownership term no longer permits a protected operation.</summary>
public sealed class LeaseLostException(LeaseIdentity identity)
    : InvalidOperationException($"Lease '{identity.Resource.Scope}/{identity.Resource.Name}' is no longer held by the expected term.");
