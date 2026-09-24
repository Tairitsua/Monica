namespace Monica.Core.Results;

/// <summary>
/// Well-known result-envelope metadata member names shared by producers and consumers across module
/// boundaries. Keep this list minimal: metadata members beyond these belong to the owning feature.
/// </summary>
public static class ResultMetadataKeys
{
    /// <summary>
    /// Response-owned exception catalog. Call-chain exception identifiers refer to this document;
    /// remote responses carry independent catalogs. Reserved for diagnostic hosts.
    /// </summary>
    public const string Diagnostics = "diagnostics";

    /// <summary>
    /// Correlation identifier of the host request that produced the envelope. Set by the result metadata
    /// filter when chain tracing is attached; safe to expose in every environment.
    /// </summary>
    public const string TraceId = "traceId";

    /// <summary>
    /// Service identity (for example the Dapr app id) of the remote target that produced the envelope.
    /// Stamped by the remote-call boundary onto every returned envelope, including classified transport
    /// failures, so call-chain nodes can label which service they invoked. Reserved diagnostic member:
    /// presentation keeps it only on hosts that expose diagnostic details.
    /// </summary>
    public const string RemoteService = "remoteService";
}
