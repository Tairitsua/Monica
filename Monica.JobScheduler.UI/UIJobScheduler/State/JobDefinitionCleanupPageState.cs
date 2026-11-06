using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using Monica.Modules;

namespace Monica.JobScheduler.UI.UIJobScheduler.State;

internal sealed class JobDefinitionCleanupPageStateFactory(JobSchedulerFacade facade, IJobSchedulerUiAccess access,
    IOptions<ModuleJobSchedulerOption> options, IStringLocalizer<JobSchedulerResource> localizer)
{
    internal JobDefinitionCleanupPageState Create() => new(facade, access, options.Value.AutonomousWork, localizer);
}

/// <summary>Owns bounded evidence, exact selection and serialized maintenance until the route drains.</summary>
internal sealed class JobDefinitionCleanupPageState(JobSchedulerFacade facade, IJobSchedulerUiAccess access,
    bool autonomous, IStringLocalizer<JobSchedulerResource> localizer) : IAsyncDisposable
{
    private readonly SchedulerOperationLifetime _lifetime = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly HashSet<JobId> _selected = [];
    private int _queryVersion;
    private JobDefinitionCleanupQuery? _acceptedQuery;
    internal event Func<Task>? Changed;
    internal bool AccessChecked { get; private set; }
    internal bool IsAuthorized { get; private set; }
    internal bool CanMaintain { get; private set; }
    internal bool Autonomous => autonomous;
    internal bool Busy { get; private set; }
    internal bool IsStale { get; private set; }
    internal bool CanWrite => autonomous && IsAuthorized && CanMaintain && !IsStale && Snapshot is not null;
    internal string? Error { get; private set; }
    internal string? OperationError { get; private set; }
    internal JobDefinitionCleanupSnapshot? Snapshot { get; private set; }
    internal JobDefinitionCleanupItem? Inspection { get; private set; }
    internal IReadOnlySet<JobId> Selected => _selected;
    internal JobDefinitionMaintenanceResult? Receipt { get; private set; }
    internal bool ReceiptIsRetirement { get; private set; }

    internal void Toggle(JobId id, bool selected) => _lifetime.TryApply(() =>
    {
        if (!selected) _selected.Remove(id);
        else if (_selected.Count < JobDefinitionCleanupRequest.MAX_DEFINITIONS) _selected.Add(id);
    });

    internal void SelectReadyPage() => _lifetime.TryApply(() =>
    {
        foreach (var item in Snapshot?.Items ?? [])
            if (item.State == JobDefinitionCleanupState.Ready && _selected.Count < JobDefinitionCleanupRequest.MAX_DEFINITIONS)
                _selected.Add(item.Id);
    });

    internal void ClearSelection() => _lifetime.TryApply(_selected.Clear);

    internal Task RefreshAsync(JobDefinitionCleanupQuery query)
    {
        var version = Interlocked.Increment(ref _queryVersion);
        return RunAsync(async token =>
        {
            _lifetime.TryApply(() =>
            {
                if (version == _queryVersion && query != _acceptedQuery) { Snapshot = null; Error = null; IsStale = true; }
            });
            if (!autonomous || !await AuthorizeAsync(token)) return false;
            var response = await facade.QueryDefinitionCleanupAsync(query, token);
            _lifetime.TryApply(() =>
            {
                if (version != _queryVersion) return;
                if (response.IsFailure()) { Error = response.Message ?? string.Empty; IsStale = true; }
                else { Snapshot = response.Data ?? throw new InvalidOperationException("The cleanup query returned no data."); _acceptedQuery = query; Error = null; IsStale = false; }
            });
            return true;
        });
    }

    internal Task CheckAccessAsync() => RunAsync(AuthorizeAsync);

    internal Task InspectAsync(JobId id) => RunAsync(async token =>
    {
        if (!autonomous || !await AuthorizeAsync(token)) return false;
        var response = await facade.PreviewDefinitionCleanupAsync([id], false, token);
        _lifetime.TryApply(() =>
        {
            if (response.IsFailure()) { Error = response.Message ?? string.Empty; Inspection = null; }
            else Inspection = (response.Data ?? throw new InvalidOperationException("The cleanup preview returned no data.")).Items.Single();
        });
        return true;
    });

    internal void CloseInspection() => _lifetime.TryApply(() => Inspection = null);

    internal Task<JobDefinitionCleanupPlan?> PreviewAsync(IReadOnlyList<JobId> ids, bool cancelQueued)
    {
        var frozen = ids.ToArray();
        return RunAsync<JobDefinitionCleanupPlan?>(async token =>
        {
            if (!await AuthorizeAsync(token) || !CanWrite) return null;
            return Accept(await facade.PreviewDefinitionCleanupAsync(frozen, cancelQueued, token));
        }, showBusy: false);
    }

    internal Task<JobOwnerRetirementPreview?> PreviewRetirementAsync(string owner) => RunAsync<JobOwnerRetirementPreview?>(async token =>
    {
        if (!await AuthorizeAsync(token) || !CanWrite) return null;
        return Accept(await facade.PreviewOwnerRetirementAsync(owner, token));
    }, showBusy: false);

    internal Task<JobDefinitionMaintenanceResult?> RemoveAsync(JobDefinitionCleanupPlan plan)
    {
        var request = plan.ToRequest();
        return RunAsync<JobDefinitionMaintenanceResult?>(async token =>
        {
            if (!await AuthorizeAsync(token) || !CanWrite) return null;
            var result = Accept(await facade.RemoveDefinitionsAsync(request, token));
            Record(result, false);
            return result;
        });
    }

    internal Task<JobDefinitionMaintenanceResult?> RetireAsync(JobOwnerRetirementPreview preview) => RunAsync<JobDefinitionMaintenanceResult?>(async token =>
    {
        if (!await AuthorizeAsync(token) || !CanWrite) return null;
        var result = Accept(await facade.RetireOwnerAsync(preview.OwnerKey, preview.Stamp, token));
        Record(result, true);
        return result;
    });

    private void Record(JobDefinitionMaintenanceResult? result, bool retirement) => _lifetime.TryApply(() =>
    {
        if (result?.Status != JobDefinitionMaintenanceStatus.Applied) return;
        Receipt = result;
        ReceiptIsRetirement = retirement;
        foreach (var id in result.Definitions) _selected.Remove(id);
        Inspection = null;
        // A successful mutation invalidates the old inventory until the next accepted capture.
        IsStale = true;
    });

    private T? Accept<T>(Result<T> response) where T : class
    {
        var value = response.Data;
        if (!response.IsFailure()) return value;
        _lifetime.TryApply(() => OperationError = response.Message ?? string.Empty);
        return null;
    }

    private async Task<bool> AuthorizeAsync(CancellationToken token)
    {
        bool authorized, maintenance;
        try
        {
            authorized = await access.IsAuthorizedAsync(token);
            maintenance = authorized && await access.IsDefinitionMaintenanceAuthorizedAsync(token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            authorized = maintenance = false;
            _lifetime.TryApply(() => Error = OperationError = localizer["Cleanup:Unavailable"]);
        }
        token.ThrowIfCancellationRequested();
        return _lifetime.TryApply(() =>
        {
            AccessChecked = true;
            IsAuthorized = authorized;
            CanMaintain = maintenance;
            if (authorized) return;
            Snapshot = null;
            Inspection = null;
            Receipt = null;
            _selected.Clear();
        }) && authorized;
    }

    private async Task<T?> RunAsync<T>(Func<CancellationToken, Task<T>> operation, bool showBusy = true)
    {
        try
        {
            var pending = _lifetime.RunAsync(async token =>
            {
                await _operations.WaitAsync(token);
                try
                {
                    // Native decisions own preview progress; keep their opener focusable while its trap mounts.
                    _lifetime.TryApply(() => { Busy = showBusy; OperationError = null; });
                    return await operation(token);
                }
                finally
                {
                    _lifetime.TryApply(() => Busy = false);
                    _operations.Release();
                }
            });
            // Subscribers may reenter this state. The producer must never wait for them while holding the gate.
            await NotifyAsync();
            return await pending;
        }
        catch (OperationCanceledException) when (_lifetime.IsRetired) { return default; }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // An unavailable authorization service cannot leave previously accepted privileges actionable.
            _lifetime.TryApply(() => { CanMaintain = false; IsStale = true; OperationError = Error = localizer["Cleanup:LoadFailed"]; });
            return default;
        }
        finally { await NotifyAsync(); }
    }

    private async Task NotifyAsync()
    {
        foreach (var handler in _lifetime.SnapshotHandlers(Changed)) await handler();
    }

    public ValueTask DisposeAsync() => _lifetime.RetireAsync(_operations.Dispose);
}
