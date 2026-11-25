using Monica.Core.ExceptionHandling.Exceptions;
using Monica.Core.Results;

namespace Monica.Core.ApplicationRoles;

/// <summary>Determines whether a startup fault can be isolated under the automatic policy.</summary>
public enum RoleCriticality { Required, Optional }

/// <summary>Chooses startup fault handling. Auto isolates optional faults on a surviving multi-role host.</summary>
public enum RoleFaultPolicyMode { Auto, FailFast, IsolateAll }

/// <summary>Describes the role on this instance. Inactive refers only to deployment placement.</summary>
public enum RoleHealthStatus { Inactive, Initializing, Healthy, Degraded, Faulted }

/// <summary>Identifies the lifecycle phase in which a role fault was observed.</summary>
public enum RoleFaultStage { StartupConfiguration, PipelineSetup, HostedServiceStartup, SeederExecution, RuntimeResource, Runtime }

/// <summary>Safe configuration evidence. Candidate validity only describes the current instance's visible sources.</summary>
public sealed record RoleConfigValidationIssueRef(string DefinitionKey, string SectionPath, string LogicalPath,
    string Problem, bool? CandidateObservedValidInThisInstance = null);

/// <summary>Describes a fault without exposing raw configuration values or exception payloads.</summary>
public sealed record RoleFaultRecord(RoleFaultStage Stage, string Summary, DateTimeOffset OccurredAt,
    IReadOnlyList<RoleConfigValidationIssueRef> ConfigurationIssues);

/// <summary>Placement, effective criticality and process-local health of one role.</summary>
public sealed record RoleHealthEntrySnapshot(string RoleId, string DomainId, string WorkloadOwner, string AssignedAppId,
    RoleCriticality DeclaredCriticality, RoleCriticality? OverrideCriticality, RoleHealthStatus Status,
    DateTimeOffset LastTransitionAt, IReadOnlyList<RoleFaultRecord> Faults)
{
    /// <summary>Gets the deployment override when configured, otherwise the catalog default.</summary>
    public RoleCriticality EffectiveCriticality => OverrideCriticality ?? DeclaredCriticality;

    /// <summary>Whether this instance is assigned this role, including a quarantined role.</summary>
    public bool IsActiveOnCurrentHost => Status != RoleHealthStatus.Inactive;

    /// <summary>Gets whether every isolated startup input has a valid candidate on this instance.</summary>
    /// <remarks>Null means that evidence is incomplete or recovery also depends on a different fault stage.</remarks>
    public bool? StartupCandidateObservedValid
    {
        get
        {
            if (Status != RoleHealthStatus.Faulted || Faults.Count == 0 ||
                Faults.Any(fault => fault.Stage != RoleFaultStage.StartupConfiguration || fault.ConfigurationIssues.Count == 0))
                return null;
            var issues = Faults.SelectMany(fault => fault.ConfigurationIssues);
            if (issues.Any(issue => issue.CandidateObservedValidInThisInstance == false)) return false;
            return issues.All(issue => issue.CandidateObservedValidInThisInstance == true) ? true : null;
        }
    }
}

/// <summary>Immutable instance-level evidence; remote placements carry no inferred remote health.</summary>
public sealed record RoleHealthSnapshot(string AppId, string AppName, string? DeploymentScope,
    RoleFaultPolicyMode ConfiguredPolicyMode, bool CapOptionalRoleHealthChecksAtDegraded,
    IReadOnlyList<RoleHealthEntrySnapshot> Roles)
{
    /// <summary>Gets the number of declared application placements, including those with no roles.</summary>
    public int ApplicationCount { get; init; }
}

/// <summary>Optional read surface provided by hosts composed with the application model.</summary>
public interface IRoleHealthStateProvider
{
    /// <summary>Captures all catalog roles. Candidate validation is opt-in and may rebind deployment inputs.</summary>
    RoleHealthSnapshot GetSnapshot(bool includeCandidateValidation = false);
    /// <summary>Returns true only when this process quarantined the role.</summary>
    bool IsRoleFaulted(string roleId);
    /// <summary>Resolves resource ownership, including a domain background role, or null for host infrastructure.</summary>
    string? GetRoleId(Type implementationType, Type? registrationOwner = null);
}

/// <summary>Publishes the outcome of one role-owned readiness check for this process.</summary>
public interface IRoleProbeStateSink
{
    /// <summary>Records or clears degradation for this registration. Recovery requires every degraded check on the role to clear.</summary>
    void Report(string roleId, string registrationName, bool degraded);
}

/// <summary>A local execution was rejected before handler activation because its role is quarantined.</summary>
public sealed class RoleUnavailableException(string roleId)
    : DisplayMessageException(MessageForRole(roleId))
{
    /// <summary>Builds the same safe admission refusal for direct, HTTP and typed invocation callers.</summary>
    public static string MessageForRole(string roleId) => $"Role '{roleId}' is unavailable on this instance.";

    /// <inheritdoc />
    public override ResultStatus ResultStatus => ResultStatus.ServiceUnavailable;

    /// <summary>Gets the local role that refused admission.</summary>
    public string RoleId { get; } = roleId;
}
