namespace Monica.Core.ApplicationRoles;

/// <summary>Decides whether startup may proceed and publishes terminal background-resource failures.</summary>
public interface IRoleFaultCoordinator
{
    /// <summary>Closes admission for an assigned role when startup policy permits isolation. Its participants drain asynchronously.</summary>
    /// <returns>False when the caller must propagate the startup failure.</returns>
    bool TryIsolateStartupFault(string roleId, RoleFaultRecord fault);

    /// <summary>Publishes a terminal resource failure before returning. A critical report closes role admission and queues a host-owned drain.</summary>
    /// <remarks>Use this form from admitted operations and participant callbacks so their current work can complete.</remarks>
    void ReportRuntimeResourceFault(string roleId, bool isRoleCriticalResource, RoleFaultRecord fault);

    /// <summary>Publishes the same failure and waits for any quarantine drain already in progress for the role.</summary>
    /// <remarks>Publish safe evidence only. Complete the reporting work item before awaiting a drain that includes that item.
    /// Cancellation releases the caller's wait. The host retains the drain until completion or disposal.</remarks>
    Task ReportRuntimeResourceFaultAsync(string roleId, bool isRoleCriticalResource, RoleFaultRecord fault,
        CancellationToken cancellationToken = default);
}

/// <summary>Receives a role's transition to quarantine, after admission has closed.</summary>
public interface IRoleLifecycleParticipant
{
    /// <summary>Stops admitting new work for this role and allows admitted operations to finish. Registered participants are host singletons.</summary>
    Task OnRoleQuarantinedAsync(string roleId, CancellationToken cancellationToken);
}

/// <summary>Observes an unhandled failure that terminated a role-owned hosted service's background loop.</summary>
public interface IRoleHostedServiceFaultObserver
{
    /// <summary>Receives the resolved role, concrete service type and original exception. Exception payloads belong in logs only.</summary>
    Task OnBackgroundExecutionFaultedAsync(string roleId, Type serviceType, Exception exception, CancellationToken cancellationToken);
}

/// <summary>Marks a background resource whose terminal failure makes its owning role unavailable.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class RoleCriticalResourceAttribute : Attribute;
