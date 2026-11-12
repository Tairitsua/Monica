namespace Monica.Locker.Leases.Metrics;

/// <summary>Defines the standard .NET instruments emitted by coordinator lease supervision.</summary>
public static class LeaseMetricNames
{
    /// <summary>The meter hosts subscribe to; instruments are owned by the host's meter factory.</summary>
    public const string MeterName = "Monica.Locker.Leases";

    /// <summary>Completed coordinator store calls, tagged by operation and result.</summary>
    /// <remarks>
    /// Operations are acquire, renew and release. Results are success, contended (acquire), lost (renew/release),
    /// cancelled, timeout or failed. Cached grants, local backoff and diagnostic reads do not emit operations.
    /// A successful store reply does not prove that the local term still has usable time or that work committed.
    /// </remarks>
    public const string Operations = "monica.locker.lease.operations";

    /// <summary>Store-call wait duration in seconds, including local deadlines, with operation and result tags.</summary>
    public const string OperationDuration = "monica.locker.lease.operation.duration";

    /// <summary>Locally supervised terms retired once, tagged by their first retirement reason.</summary>
    /// <remarks>
    /// Reasons are expired, renewal_rejected, renewal_timeout, renewal_failed and stopped. Normal shutdown uses
    /// stopped; it is not ownership-loss evidence. Late replies and repeated disposal do not add retirements.
    /// </remarks>
    public const string TermsEnded = "monica.locker.lease.terms.ended";
}
