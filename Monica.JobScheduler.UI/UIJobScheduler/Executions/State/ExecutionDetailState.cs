using Monica.Core.Results;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.Support;

namespace Monica.JobScheduler.UI.UIJobScheduler.Executions.State;

/// <summary>Owns full retained evidence and operator requests for one immutable exact execution identifier.</summary>
/// <remarks>
/// Create a new state when the inspector identity changes. Async admission, including authorization, belongs to this
/// session; retirement cancels and drains actual work. The component owns clipboard and native confirmation work
/// separately and must not borrow this state's cancellation source.
/// </remarks>
internal sealed class ExecutionDetailState(
    JobSchedulerFacade facade,
    IJobSchedulerUiAccess access,
    TimeProvider timeProvider,
    string instanceId) : IAsyncDisposable
{
    private readonly SchedulerOperationLifetime _lifetime = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private int _loadVersion;
    private int _accessVersion;
    private int _pendingMutations;

    /// <summary>Raised outside owner locks when consumers should render the current detail evidence.</summary>
    public event Func<Task>? StateChanged;

    /// <summary>Gets the exact, unnormalized execution identifier fixed at construction.</summary>
    public string InstanceId { get; } = ValidateIdentifier(instanceId);

    /// <summary>Gets the complete retained detail snapshot, including history, when accepted.</summary>
    public JobExecutionInstance? Execution { get; private set; }

    /// <summary>Gets whether authorization has been evaluated.</summary>
    public bool AccessChecked { get; private set; }

    /// <summary>Gets whether current authorization evidence permits inspection.</summary>
    public bool IsAuthorized { get; private set; }

    /// <summary>Gets whether the newest admitted detail read is pending or running.</summary>
    public bool IsLoading { get; private set; } = true;

    /// <summary>Gets whether admitted cancellation and its post-mutation read are pending or running.</summary>
    public bool IsMutating { get; private set; }

    /// <summary>Gets whether a successful read accepted an execution or a confirmed missing result.</summary>
    public bool HasLoadedSnapshot { get; private set; }

    /// <summary>Gets whether the newest read failed instead of confirming available or missing evidence.</summary>
    public bool IsQueryFailed { get; private set; }

    /// <summary>Gets whether the execution was absent in the accepted successful read.</summary>
    public bool IsNotFound { get; private set; }

    /// <summary>Gets the latest read or authorization error, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Gets when the accepted detail read completed.</summary>
    public DateTimeOffset? ObservedAtUtc { get; private set; }

    /// <summary>Gets whether retirement has stopped new async work.</summary>
    public bool IsDisposed => _lifetime.IsRetired;

    /// <summary>Loads full retained evidence for this state's immutable execution identifier.</summary>
    /// <exception cref="OperationCanceledException">The owning state retired.</exception>
    public Task InitializeAsync() => RefreshAsync();

    /// <summary>Reloads full retained evidence, preserving prior same-ID evidence only on a read failure.</summary>
    /// <remarks>Authorization denial or provider faults clear inaccessible evidence.</remarks>
    /// <exception cref="OperationCanceledException">The owning state retired.</exception>
    public Task RefreshAsync() => _lifetime.RunAsync(async token =>
    {
        var version = 0;
        if (!_lifetime.TryApply(() =>
            {
                version = ++_loadVersion;
                IsLoading = true;
                IsQueryFailed = false;
                Error = null;
            })) throw RetiredCancellation();
        await LoadAsync(version, token);
    });

    /// <summary>Rechecks current access before the caller opens an operator confirmation.</summary>
    /// <remarks>
    /// The confirmed mutation performs its own authorization check. Denial returns false; a provider fault also
    /// returns false, clears inaccessible evidence, and retains its diagnostic in <see cref="Error"/>.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The owning state retired.</exception>
    public Task<bool> ReauthorizeAsync() => _lifetime.RunAsync(async token =>
    {
        var authorization = await EnsureAuthorizedAsync(token);
        await NotifyChangedAsync();
        return authorization.IsAuthorized;
    });

    /// <summary>Reauthorizes and requests durable cancellation, then separately reauthorizes a full detail refresh.</summary>
    /// <remarks>
    /// The returned status is the actual store outcome. Cooperative cancellation does not imply terminal completion.
    /// A subsequent read failure retains the mutation outcome and reports the failed read separately in this state.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The owning state retired before actual work completed.</exception>
    public Task<ExecutionCancellationUiResult> RequestCancellationAsync() =>
        _lifetime.RunAsync<ExecutionCancellationUiResult>(async token =>
        {
            if (!_lifetime.TryApply(() => { _pendingMutations++; IsMutating = true; })) throw RetiredCancellation();
            try
            {
                token.ThrowIfCancellationRequested();
                await NotifyChangedAsync();
                await _mutationGate.WaitAsync(token);
                JobCancellationStatus status;
                try
                {
                    var authorization = await EnsureAuthorizedAsync(token);
                    if (!authorization.IsAuthorized) return new(false, null, authorization.Error);
                    var result = await facade.CancelExecutionAsync(InstanceId, cancellationToken: token);
                    token.ThrowIfCancellationRequested();
                    var error = result.WithoutData();
                    var cancellation = result.Data;
                    if (result.IsFailure() || cancellation is null) return new(true, null, error.Message ?? "The cancellation request returned no data.");
                    status = cancellation.Status;
                }
                finally { _mutationGate.Release(); }
                await RefreshAsync();
                return new(true, status, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { return new(IsAuthorized, null, exception.Message); }
            finally
            {
                if (_lifetime.TryApply(() => { _pendingMutations--; IsMutating = _pendingMutations > 0; }))
                    await NotifyChangedAsync();
            }
        });

    /// <summary>Cancels and drains actual admitted work before releasing serialization resources.</summary>
    /// <remarks>Repeated calls share the same completion, including unexpected faults.</remarks>
    /// <exception cref="AggregateException">Cancellation callbacks, admitted work, or cleanup failed unexpectedly.</exception>
    public ValueTask DisposeAsync()
    {
        StateChanged = null;
        return _lifetime.RetireAsync(() =>
        {
            try { _loadGate.Dispose(); }
            finally { _mutationGate.Dispose(); }
        });
    }

    private async Task LoadAsync(int version, CancellationToken token)
    {
        var entered = false;
        int? authorizedVersion = null;
        try
        {
            token.ThrowIfCancellationRequested();
            await NotifyChangedAsync();
            await _loadGate.WaitAsync(token);
            entered = true;
            var authorization = await EnsureAuthorizedAsync(token);
            authorizedVersion = authorization.Version;
            if (!authorization.IsAuthorized)
            {
                _lifetime.TryApply(() => { if (version == _loadVersion) IsQueryFailed = authorization.Error is not null; });
                return;
            }
            var result = await facade.GetExecutionAsync(InstanceId, token);
            token.ThrowIfCancellationRequested();
            var error = result.WithoutData();
            var execution = result.Data;
            if (result.IsFailure())
            {
                _lifetime.TryApply(() =>
                {
                    if (version != _loadVersion || !CanAcceptAccess(authorization.Version)) return;
                    Error = error.Message;
                    IsQueryFailed = true;
                });
                return;
            }
            var observedAtUtc = timeProvider.GetUtcNow();
            if (!_lifetime.TryApply(() =>
                {
                    if (version != _loadVersion || !CanAcceptAccess(authorization.Version)) return;
                    Execution = execution;
                    IsNotFound = execution is null;
                    HasLoadedSnapshot = true;
                    IsQueryFailed = false;
                    Error = null;
                    ObservedAtUtc = observedAtUtc;
                })) throw RetiredCancellation();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _lifetime.TryApply(() =>
            {
                if (version != _loadVersion || (authorizedVersion is { } accessVersion && !CanAcceptAccess(accessVersion))) return;
                Error = exception.Message;
                IsQueryFailed = true;
            });
        }
        finally
        {
            if (entered) _loadGate.Release();
            if (_lifetime.TryApply(() => { if (version == _loadVersion) IsLoading = false; }))
                await NotifyChangedAsync();
        }
    }

    private async Task<(bool IsAuthorized, string? Error, int Version)> EnsureAuthorizedAsync(CancellationToken token)
    {
        var version = 0;
        if (!_lifetime.TryApply(() => version = _accessVersion)) throw RetiredCancellation();
        try
        {
            var authorized = await access.IsAuthorizedAsync(token);
            token.ThrowIfCancellationRequested();
            var accepted = false;
            if (!_lifetime.TryApply(() =>
                {
                    if (!authorized)
                    {
                        _accessVersion++;
                        IsAuthorized = false;
                        AccessChecked = true;
                        ClearEvidence();
                        Error = null;
                        return;
                    }
                    if (version != _accessVersion) return;
                    IsAuthorized = true;
                    AccessChecked = true;
                    accepted = true;
                })) throw RetiredCancellation();
            return (accepted, null, version);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            if (!_lifetime.TryApply(() =>
                {
                    _accessVersion++;
                    IsAuthorized = false;
                    AccessChecked = true;
                    ClearEvidence();
                    Error = exception.Message;
                })) throw RetiredCancellation();
            return (false, exception.Message, version);
        }
    }

    private bool CanAcceptAccess(int version) => IsAuthorized && version == _accessVersion;

    private void ClearEvidence()
    {
        Execution = null;
        HasLoadedSnapshot = false;
        IsQueryFailed = false;
        IsNotFound = false;
        ObservedAtUtc = null;
    }

    private async Task NotifyChangedAsync()
    {
        foreach (var handler in _lifetime.SnapshotHandlers(StateChanged)) await handler();
    }

    private static string ValidateIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }

    private static OperationCanceledException RetiredCancellation() => new(new CancellationToken(canceled: true));
}
