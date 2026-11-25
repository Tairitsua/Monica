namespace Monica.Core.ApplicationRoles;

/// <summary>Application-owned decisions used by configuration modules before registration callbacks execute.</summary>
public interface IRoleStartupFaultPolicy
{
    /// <summary>Completes initialization of admitted roles when the Generic Host signals ApplicationStarted.</summary>
    void MarkHostStarted();

    /// <summary>Validates declared role identifiers and returns the subset assigned to the current AppId.</summary>
    IReadOnlyList<string> GetAssignedRoles(IReadOnlyList<string> requiredByRoles);

    /// <summary>Records a scoped validation failure. Returns false when host startup must fail.</summary>
    bool RecordConfigurationFault(string roleId, RoleFaultRecord fault, Func<bool> validateVisibleCandidate);
}

/// <summary>Participates in application-model preflight after placement freezes and before role registrations.</summary>
public interface IRoleStartupPreflight
{
    /// <summary>Validates frozen configuration and applies startup policy synchronously, without resolving DI services.</summary>
    void ValidateRoleStartup(MonicaApplication application, IRoleStartupFaultPolicy policy);
}
