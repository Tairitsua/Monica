using System.Collections.Frozen;
using Microsoft.Extensions.Configuration;
using Monica.Core.ApplicationRoles;

namespace Monica.ApplicationModel.Services;

internal sealed partial class CompiledApplicationModel : IRoleHealthStateProvider, IRoleStartupFaultPolicy, IRoleProbeStateSink
{
    private readonly Dictionary<string, RoleCriticality> _criticalityOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoleState> _roleStates = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _preparedAt = DateTimeOffset.UtcNow;
    private readonly Lock _healthGate = new();
    private long _startedAtTicks;
    private RoleFaultPolicyMode _mode;
    private readonly string? _deploymentScope;
    private bool _capOptionalChecks;
    private bool _preflightClosed;
    private string[] _assignedRoles = [];
    private volatile WorkloadPlacement _workloadPlacement = new(FrozenSet<string>.Empty, [], []);
    private bool _stopOnSingleRoleFault;

    internal bool StopAfterRuntimeQuarantine => _assignedRoles.Length == 1
        && (_mode == RoleFaultPolicyMode.FailFast || _mode == RoleFaultPolicyMode.Auto && _stopOnSingleRoleFault);

    private void InitializeHealth(IConfiguration startup)
    {
        var policy = startup.GetSection("Application:RoleFaultPolicy");
        _mode = ParsePolicyValue(policy["Mode"], RoleFaultPolicyMode.Auto, "Mode");
        _capOptionalChecks = policy.GetValue("CapOptionalRoleHealthChecksAtDegraded", _mode == RoleFaultPolicyMode.Auto);
        _stopOnSingleRoleFault = policy.GetValue<bool>("StopHostOnSingleRoleRuntimeFault");
        foreach (var item in policy.GetSection("RoleCriticalityOverrides").GetChildren())
        {
            if (!Roles.ContainsKey(item.Key) || string.IsNullOrWhiteSpace(item.Value))
                throw new InvalidOperationException($"Unknown role or missing criticality at '{item.Path}'.");
            _criticalityOverrides.Add(item.Key, ParsePolicyValue(item.Value, RoleCriticality.Required, item.Path));
        }

        _assignedRoles = Roles.Keys.Where(IsRoleActive).Order(StringComparer.Ordinal).ToArray();
        PublishPlacement(FrozenSet<string>.Empty);
    }

    private static TEnum ParsePolicyValue<TEnum>(string? value, TEnum fallback, string key) where TEnum : struct, Enum
    {
        if (value is null) return fallback;
        if (!Enum.GetNames<TEnum>().Contains(value, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown value for application role policy '{key}'.");
        return Enum.Parse<TEnum>(value, ignoreCase: true);
    }

    private void PublishPlacement(FrozenSet<string> quarantined)
    {
        string Owner(string id) => Roles[id].WorkloadOwner ?? id;
        _workloadPlacement = new(quarantined,
            Array.AsReadOnly(_assignedRoles.Where(id => !quarantined.Contains(id)).Select(Owner).Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(quarantined.Select(Owner).Order(StringComparer.Ordinal).ToArray()));
    }

    internal void ClosePreflight() => _preflightClosed = true;

    public void MarkHostStarted() => Interlocked.CompareExchange(ref _startedAtTicks, DateTimeOffset.UtcNow.UtcTicks, 0);

    public void Report(string roleId, string registrationName, bool degraded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationName);
        lock (_healthGate)
        {
            if (!IsRoleActive(roleId) || IsRoleFaulted(roleId)) return;
            if (!_roleStates.TryGetValue(roleId, out var state))
            {
                if (!degraded) return;
                _roleStates.Add(roleId, state = new RoleState());
            }
            var wasDegraded = state.IsDegraded;
            if (degraded) state.DegradedChecks.Add(registrationName);
            else state.DegradedChecks.Remove(registrationName);
            if (wasDegraded != state.IsDegraded)
                state.LastTransitionAt = DateTimeOffset.UtcNow;
        }
    }

    public bool IsRoleFaulted(string roleId) => _workloadPlacement.Quarantined.Contains(roleId);

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
        lock (_healthGate)
        {
            var mayIsolate = CanIsolateStartupFault(roleId);
            AddFault(roleId, fault, quarantine: true, validateVisibleCandidate);
            return mayIsolate;
        }
    }

    internal (bool Isolated, bool Transitioned) RecordStartupResourceFault(string roleId, RoleFaultRecord fault)
    {
        lock (_healthGate)
        {
            if (!IsRoleActive(roleId) || !CanIsolateStartupFault(roleId)) return (false, false);
            return (true, AddFault(roleId, fault, quarantine: true));
        }
    }

    private bool CanIsolateStartupFault(string roleId) =>
        _mode == RoleFaultPolicyMode.IsolateAll || _mode == RoleFaultPolicyMode.Auto
        && _assignedRoles.Length > _workloadPlacement.Quarantined.Count + (IsRoleFaulted(roleId) ? 0 : 1)
        && _criticalityOverrides.GetValueOrDefault(roleId, Roles[roleId].Criticality) == RoleCriticality.Optional;

    internal bool RecordRuntimeFault(string roleId, RoleFaultRecord fault, bool critical)
    {
        lock (_healthGate)
        {
            if (!IsRoleActive(roleId))
                throw new InvalidOperationException($"Runtime faults require an assigned role, received '{roleId}'.");
            if (IsRoleFaulted(roleId)) return false;
            return AddFault(roleId, fault, critical);
        }
    }

    private bool AddFault(string roleId, RoleFaultRecord fault, bool quarantine, Func<bool>? candidate = null)
    {
        if (!_roleStates.TryGetValue(roleId, out var state))
            _roleStates.Add(roleId, state = new RoleState());
        var wasDegraded = state.IsDegraded;
        var transitioned = quarantine && !IsRoleFaulted(roleId);
        if (candidate is not null || transitioned || !state.Faults.Any(entry =>
                entry.Record.Stage == fault.Stage && entry.Record.Summary == fault.Summary))
            state.Faults.Add(new FaultEvidence(fault with { ConfigurationIssues = Array.AsReadOnly(fault.ConfigurationIssues.ToArray()) }, candidate));
        if (transitioned)
            PublishPlacement(_workloadPlacement.Quarantined.Append(roleId).ToFrozenSet(StringComparer.Ordinal));
        if (transitioned || !wasDegraded) state.LastTransitionAt = fault.OccurredAt;
        return transitioned;
    }

    public RoleHealthSnapshot GetSnapshot(bool includeCandidateValidation = false)
    {
        var started = Interlocked.Read(ref _startedAtTicks);
        (RoleHealthEntrySnapshot Role, FaultEvidence[] Faults)[] captured;
        lock (_healthGate)
            captured = Roles.Values.OrderBy(role => role.Id, StringComparer.Ordinal).Select(role =>
        {
            var state = _roleStates.GetValueOrDefault(role.Id);
            var status = !IsRoleActive(role.Id) ? RoleHealthStatus.Inactive : IsRoleFaulted(role.Id) ? RoleHealthStatus.Faulted
                : state?.IsDegraded == true ? RoleHealthStatus.Degraded : started == 0 ? RoleHealthStatus.Initializing : RoleHealthStatus.Healthy;
            var changedAt = status == RoleHealthStatus.Healthy ? new DateTimeOffset(started, TimeSpan.Zero) : _preparedAt;
            if (state is not null && state.LastTransitionAt > changedAt) changedAt = state.LastTransitionAt;
            return (new RoleHealthEntrySnapshot(role.Id, role.DomainId, role.WorkloadOwner ?? role.Id,
                ResolveRoleAppId(role.Id), role.Criticality, _criticalityOverrides.TryGetValue(role.Id, out var value) ? value : null,
                status, changedAt, []), state?.Faults.ToArray() ?? []);
        }).ToArray();

        // Candidate validation can rebind external configuration. Never run it while holding the state lock.
        var observed = new Dictionary<Func<bool>, bool>();
        var entries = captured.Select(entry => entry.Role with
        {
            Faults = Array.AsReadOnly(entry.Faults.Select(ProjectFault).ToArray())
        }).ToArray();
        return new RoleHealthSnapshot(AppId, Deployment.AppName, _deploymentScope, _mode, _capOptionalChecks, Array.AsReadOnly(entries))
        {
            ApplicationCount = Deployment.Applications.Count
        };

        RoleFaultRecord ProjectFault(FaultEvidence failure)
        {
            if (!includeCandidateValidation || failure.ValidateCandidate is null) return failure.Record;
            if (!observed.TryGetValue(failure.ValidateCandidate, out var valid))
                observed.Add(failure.ValidateCandidate, valid = failure.ValidateCandidate());
            return failure.Record with { ConfigurationIssues = Array.AsReadOnly(failure.Record.ConfigurationIssues
                .Select(issue => issue with { CandidateObservedValidInThisInstance = valid }).ToArray()) };
        }
    }

    private sealed record WorkloadPlacement(FrozenSet<string> Quarantined, IReadOnlyCollection<string> ActiveOwners,
        IReadOnlyCollection<string> SuspendedOwners);

    private sealed class RoleState
    {
        internal List<FaultEvidence> Faults { get; } = [];
        internal HashSet<string> DegradedChecks { get; } = new(StringComparer.Ordinal);
        internal bool IsDegraded => DegradedChecks.Count != 0 || Faults.Count != 0;
        internal DateTimeOffset LastTransitionAt { get; set; }
    }

    private sealed record FaultEvidence(RoleFaultRecord Record, Func<bool>? ValidateCandidate);
}
