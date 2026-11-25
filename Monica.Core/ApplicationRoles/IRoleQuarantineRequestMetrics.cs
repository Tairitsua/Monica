namespace Monica.Core.ApplicationRoles;

/// <summary>Records requests refused by the admission boundary for a quarantined local role.</summary>
/// <remarks>Implemented by the application model. Record once at the gate that short-circuits a request.</remarks>
public interface IRoleQuarantineRequestMetrics
{
    /// <summary>Records an incoming HTTP request rejected before local execution.</summary>
    void RecordHttpRejection(string roleId);

    /// <summary>Records a typed invocation or direct mediator call rejected before local execution.</summary>
    void RecordLocalRejection(string roleId);
}
