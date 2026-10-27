using Monica.Core.Results;
using Monica.ServiceDiscovery.Facades;
using Monica.ServiceDiscovery.Models;

namespace Monica.ServiceDiscovery.UIServiceDiscovery.State;

/// <summary>
/// Retains current-host preferences and the last successful detached capture. The consuming component
/// owns polling, serialization, cancellation and disposal; this borrowed scoped state owns no timer.
/// </summary>
public sealed class CurrentInstanceInfoState(ServiceDiscoveryFacade serviceDiscoveryFacade)
{
    public bool IsLoading { get; private set; }
    public bool AutoRefreshEnabled { get; set; } = true;
    public int RefreshInterval { get; set; } = 3;
    public string? ErrorMessage { get; private set; }
    public CurrentInstanceSnapshot? Snapshot { get; private set; }
    public DateTime? CapturedAt { get; private set; }

    /// <summary>
    /// Reads host and provider evidence without discarding a previous successful capture on failure.
    /// The owner must serialize calls and drain this operation before retiring its cancellation source.
    /// </summary>
    public async Task<Res<CurrentInstanceSnapshot>> RefreshAsync(
        bool showLoading = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (showLoading) IsLoading = true;
        try
        {
            var result = await serviceDiscoveryFacade.CaptureCurrentInstanceAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsFailed(out var error, out var snapshot))
            {
                ErrorMessage = error.Message;
                return result;
            }

            Snapshot = new CurrentInstanceSnapshot
            {
                CurrentInstance = ServiceDiscoverySnapshot.Copy(snapshot.CurrentInstance),
                IsLeaderElectionEnabled = snapshot.IsLeaderElectionEnabled,
                CurrentLeaderStatus = snapshot.CurrentLeaderStatus,
                IsLeader = snapshot.IsLeader,
                LeaderBecomeTime = snapshot.LeaderBecomeTime,
                CurrentTermId = snapshot.CurrentTermId,
                ClusterLeaderState = snapshot.ClusterLeaderState is { } leader
                    ? new LeaderState { AppId = leader.AppId, InstanceId = leader.InstanceId, BecomeLeaderTime = leader.BecomeLeaderTime }
                    : null
            };
            CapturedAt = DateTime.UtcNow;
            ErrorMessage = null;
            return result;
        }
        finally
        {
            if (showLoading) IsLoading = false;
        }
    }
}