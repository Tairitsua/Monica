using Monica.Core.ApplicationRoles;
using Monica.Repository.Outbox.Models;

namespace Monica.Repository.Outbox.Services;

/// <summary>Host-owned operational settings for one durable event store.</summary>
public sealed record OutboxRegistration<TDbContext>(RepositoryOutboxOptions Options)
{
    internal OutboxRoleAdmission<TDbContext>? RoleAdmission { get; init; }
}

// A passive or remote context can be registered without a usable local DbContext factory.
// The coordinator can close its admission without ever constructing the dispatcher.
internal sealed class OutboxRoleAdmission<TDbContext>(IRoleHealthStateProvider? health = null) : IRoleLifecycleParticipant
{
    private readonly string? _roleId = health?.GetRoleId(typeof(TDbContext));
    private readonly RoleWorkAdmission _deliveries = new();
    private readonly TaskCompletionSource _quarantined = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool IsQuarantined => _roleId is not null && health!.IsRoleFaulted(_roleId);
    internal Task QuarantineStarted => _quarantined.Task;

    internal void EnsureAvailable()
    {
        if (IsQuarantined) throw new RoleUnavailableException(_roleId!);
    }

    internal RoleWorkAdmission.Lease AdmitDelivery()
    {
        EnsureAvailable();
        return _deliveries.TryEnter() ?? throw new RoleUnavailableException(_roleId!);
    }

    public Task OnRoleQuarantinedAsync(string roleId, CancellationToken cancellationToken)
    {
        if (roleId != _roleId) return Task.CompletedTask;
        _quarantined.TrySetResult();
        return DrainAsync(cancellationToken);
    }

    internal Task DrainAsync(CancellationToken cancellationToken)
        => _deliveries.CloseAndDrainAsync().WaitAsync(cancellationToken);
}
