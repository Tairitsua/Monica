using Microsoft.Extensions.Configuration;
using Monica.Core.ApplicationRoles;

namespace Monica.ApplicationModel.Services;

internal sealed partial class CompiledApplicationModel : IRoleHealthStateProvider, IRoleStartupFaultPolicy, IRoleProbeStateSink
{
    private readonly Dictionary<string, RoleCriticality> _criticalityOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<StartupFault>> _roleFaults = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _preparedAt = DateTimeOffset.UtcNow;
    private readonly Lock _probeGate = new();
    private readonly Dictionary<string, RoleProbeState> _probeStates = new(StringComparer.Ordinal);
    private long _startedAtTicks;
    private RoleFaultPolicyMode _mode;
    private readonly string? _deploymentScope;
    private bool _capOptionalChecks;
    private bool _preflightClosed;
    private string[] _assignedRoles = [];
    private IReadOnlyCollection<string> _activeWorkloadOwners = [];
    private IReadOnlyCollection<string> _suspendedWorkloadOwners = [];

    private void InitializeHealth(IConfiguration startup)
    {
        var policy = startup.GetSection("Application:RoleFaultPolicy");
        _mode = ParsePolicyValue(policy["Mode"], RoleFaultPolicyMode.Auto, "Mode");
        _capOptionalChecks = policy.GetValue("CapOptionalRoleHealthChecksAtDegraded", _mode == RoleFaultPolicyMode.Auto);
        foreach (var item in policy.GetSection("RoleCriticalityOverrides").GetChildren())
        {
            if (!Roles.ContainsKey(item.Key) || string.IsNullOrWhiteSpace(item.Value))
                throw new InvalidOperationException($"Unknown role or missing criticality at '{item.Path}'.");
            _criticalityOverrides.Add(item.Key, ParsePolicyValue(item.Value, RoleCriticality.Required, item.Path));
        }

        _assignedRoles = Roles.Keys.Where(IsRoleActive).Order(StringComparer.Ordinal).ToArray();
        UpdateWorkloadOwners();
    }

    private static TEnum ParsePolicyValue<TEnum>(string? value, TEnum fallback, string key) where TEnum : struct, Enum
    {
        if (value is null) return fallback;
        if (!Enum.GetNames<TEnum>().Contains(value, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown value for application role policy '{key}'.");
        return Enum.Parse<TEnum>(value, ignoreCase: true);
    }

    private void UpdateWorkloadOwners()
    {
        string Owner(string id) => Roles[id].WorkloadOwner ?? id;
        _activeWorkloadOwners = Array.AsReadOnly(_assignedRoles.Where(id => !IsRoleFaulted(id)).Select(Owner).Order(StringComparer.Ordinal).ToArray());
        _suspendedWorkloadOwners = Array.AsReadOnly(_assignedRoles.Where(IsRoleFaulted).Select(Owner).Order(StringComparer.Ordinal).ToArray());
    }

    internal void ClosePreflight() => _preflightClosed = true;

    public void MarkHostStarted() => Interlocked.CompareExchange(ref _startedAtTicks, DateTimeOffset.UtcNow.UtcTicks, 0);

    public void Report(string roleId, string registrationName, bool degraded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationName);
        if (!IsRoleActive(roleId) || IsRoleFaulted(roleId)) return;
        lock (_probeGate)
        {
            if (!_probeStates.TryGetValue(roleId, out var state))
            {
                if (!degraded) return;
                _probeStates.Add(roleId, state = new RoleProbeState());
            }
            var wasDegraded = state.DegradedChecks.Count != 0;
            if (degraded) state.DegradedChecks.Add(registrationName);
            else state.DegradedChecks.Remove(registrationName);
            if (wasDegraded != (state.DegradedChecks.Count != 0))
                state.LastTransitionAt = DateTimeOffset.UtcNow;
        }
    }

    public bool IsRoleFaulted(string roleId) => _roleFaults.ContainsKey(roleId);

    public string? GetRoleId(Type implementationType, Type? registrationOwner = null)
    {
        var implementation = GetOwnership(implementationType);
        var declared = registrationOwner is null ? default : GetOwnership(registrationOwner);
        ValidateOwnership(implementation, declared);
        return implementation.Role?.Id ?? declared.Role?.Id ?? (declared.Domain ?? implementation.Domain)?.BackgroundRole;
    }

    public IReadOnlyList<string> GetAssignedRoles(IReadOnlyList<string> requiredByRoles)
    {
        if (requiredByRoles.Count == 0)
            throw new InvalidOperationException("RequiredByRoles must name at least one role when supplied.");
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in requiredByRoles)
            if (string.IsNullOrWhiteSpace(id) || !Roles.ContainsKey(id) || !roles.Add(id))
                throw new InvalidOperationException($"RequiredByRoles contains an unknown or duplicate role '{id}'.");
        return roles.Where(IsRoleActive).Order(StringComparer.Ordinal).ToArray();
    }

    public bool RecordConfigurationFault(string roleId, RoleFaultRecord fault, Func<bool> validateVisibleCandidate)
    {
        if (_preflightClosed || !IsRoleActive(roleId))
            throw new InvalidOperationException("Configuration faults must belong to an assigned role during startup preflight.");
        if (!_roleFaults.TryGetValue(roleId, out var faults))
        {
            _roleFaults.Add(roleId, faults = []);
            UpdateWorkloadOwners();
        }
        faults.Add(new StartupFault(fault, validateVisibleCandidate));
        return _mode == RoleFaultPolicyMode.IsolateAll || _mode == RoleFaultPolicyMode.Auto
            && _assignedRoles.Length > _roleFaults.Count
            && _criticalityOverrides.GetValueOrDefault(roleId, Roles[roleId].Criticality) == RoleCriticality.Optional;
    }

    public RoleHealthSnapshot GetSnapshot(bool includeCandidateValidation = false)
    {
        var started = Interlocked.Read(ref _startedAtTicks);
        var observed = new Dictionary<Func<bool>, bool>();
        var entries = Roles.Values.OrderBy(role => role.Id, StringComparer.Ordinal).Select(role =>
        {
            var faults = _roleFaults.GetValueOrDefault(role.Id);
            var status = !IsRoleActive(role.Id) ? RoleHealthStatus.Inactive : faults is not null ? RoleHealthStatus.Faulted
                : started == 0 ? RoleHealthStatus.Initializing : RoleHealthStatus.Healthy;
            var changedAt = faults?.First().Record.OccurredAt ?? (status == RoleHealthStatus.Healthy
                ? new DateTimeOffset(started, TimeSpan.Zero) : _preparedAt);
            if (status == RoleHealthStatus.Healthy)
            {
                lock (_probeGate)
                {
                    if (_probeStates.TryGetValue(role.Id, out var probe))
                    {
                        if (probe.DegradedChecks.Count != 0) status = RoleHealthStatus.Degraded;
                        if (probe.LastTransitionAt > changedAt) changedAt = probe.LastTransitionAt;
                    }
                }
            }
            return new RoleHealthEntrySnapshot(role.Id, role.DomainId, role.WorkloadOwner ?? role.Id,
                ResolveRoleAppId(role.Id), role.Criticality, _criticalityOverrides.TryGetValue(role.Id, out var value) ? value : null,
                status, changedAt, Array.AsReadOnly(faults?.Select(ProjectFault).ToArray() ?? []));
        }).ToArray();
        return new RoleHealthSnapshot(AppId, Deployment.AppName, _deploymentScope, _mode, _capOptionalChecks, Array.AsReadOnly(entries))
        {
            ApplicationCount = Deployment.Applications.Count
        };

        RoleFaultRecord ProjectFault(StartupFault failure)
        {
            if (!includeCandidateValidation) return failure.Record;
            if (!observed.TryGetValue(failure.ValidateCandidate, out var valid))
                observed.Add(failure.ValidateCandidate, valid = failure.ValidateCandidate());
            return failure.Record with { ConfigurationIssues = Array.AsReadOnly(failure.Record.ConfigurationIssues
                .Select(issue => issue with { CandidateObservedValidInThisInstance = valid }).ToArray()) };
        }
    }

    private sealed class RoleProbeState
    {
        internal HashSet<string> DegradedChecks { get; } = new(StringComparer.Ordinal);
        internal DateTimeOffset LastTransitionAt { get; set; }
    }

    private sealed record StartupFault(RoleFaultRecord Record, Func<bool> ValidateCandidate);
}
