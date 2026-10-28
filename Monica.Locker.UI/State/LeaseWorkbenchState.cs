using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Locker.Facades;
using Monica.Locker.Leases;
using Monica.Locker.Leases.Diagnostics;
using Monica.Modules;

namespace Monica.Locker.UI.State;

/// <summary>Retains detached evidence and preferences for one circuit; route components own admitted work.</summary>
public sealed class LeaseWorkbenchState(LeaseDiagnosticsFacade facade, IOptions<ModuleLockerUIOption> options)
{
    /// <summary>Supported inventory refresh intervals, including Off.</summary>
    public static IReadOnlyList<int> RefreshIntervals { get; } = [15, 30, 60, 0];
    public int RefreshSeconds { get; set; } = RefreshIntervals.Contains(options.Value.AutoRefreshSeconds) ? options.Value.AutoRefreshSeconds : 30;
    public IReadOnlyList<LeaseAuthorityEvidence> Authorities { get; private set; } = [];
    public bool HasLoaded { get; private set; }
    public bool Failed { get; private set; }
    public bool AccessDenied { get; private set; }
    public LeaseResourceEvidence? Exact { get; private set; }
    public bool ExactFailed { get; private set; }
    public (string Authority, LeaseResource Resource)? Selection { get; private set; }
    public DateTimeOffset? OldestObservation => Authorities.Where(item => item.Snapshot is not null)
        .Select(item => (DateTimeOffset?)item.Snapshot!.ObservedAtUtc).Min();
    public IReadOnlyList<LeaseRow> Rows { get; private set; } = [];

    public async Task RefreshAsync(CancellationToken token)
    {
        var result = await facade.GetInventoryAsync(token);
        token.ThrowIfCancellationRequested();
        AccessDenied = result.Status == Core.Results.ResStatus.Forbidden;
        Failed = !result.IsOk();
        if (AccessDenied) { Authorities = []; Rows = []; Exact = null; HasLoaded = false; }
        else if (result.IsOk() && result.Data is { } authorities)
        {
            Authorities = authorities;
            Rows = authorities.SelectMany(source => source.Snapshot?.Leases
                .Select(record => new LeaseRow(source.Authority, record, source.Snapshot.ObservedAtUtc)) ?? []).ToArray();
            HasLoaded = true;
        }
        if (!AccessDenied && Selection is { } selection) await SelectAsync(selection.Authority, selection.Resource, token);
    }

    public async Task SelectAsync(string authorityId, LeaseResource resource, CancellationToken token)
    {
        if (Selection != (authorityId, resource)) Exact = null;
        Selection = (authorityId, resource);
        var result = await facade.GetResourceAsync(authorityId, resource, token);
        token.ThrowIfCancellationRequested();
        ExactFailed = !result.IsOk();
        if (result.Status == Core.Results.ResStatus.Forbidden) { AccessDenied = true; Authorities = []; Rows = []; Exact = null; HasLoaded = false; }
        else if (result.IsOk()) Exact = result.Data;
    }

    public void ClearSelection() { Selection = null; Exact = null; ExactFailed = false; }
}

/// <summary>Interprets a retained grant only against its authority observation time.</summary>
public sealed record LeaseRow(LeaseAuthority Authority, LeaseObservation Record, DateTimeOffset ObservedAtUtc)
{
    public bool Expired => Record.ExpiresAtUtc <= ObservedAtUtc;
    public double RemainingSeconds => (Record.ExpiresAtUtc - ObservedAtUtc).TotalSeconds;
}
