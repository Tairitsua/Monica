using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.DevOps.Git.Facades;
using Monica.DevOps.Git.Models;
using Monica.Modules;

namespace Monica.DevOps.Git.State;

/// <summary>Owns one UI visit's accepted Git evidence and admitted operations. The facade is borrowed.</summary>
/// <remarks>Call from the component dispatcher. Retire before releasing component resources. Retirement stops
/// presentation and polling, but joins mutations because the facade has no cancellation contract.</remarks>
public sealed class GitWorkspaceController(GitFacade facade, IOptions<ModuleGitOption> options) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _users = [];
    private Task? _retirement;
    private long _commandVersion;

    /// <summary>Requests presentation of current accepted state; no notifications occur after retirement.</summary>
    public event Action? Changed;
    /// <summary>Gets accepted snapshots, unaffected by filters or optimistic command intent.</summary>
    public IReadOnlyList<GitRepositoryStatus> Repositories { get; private set; } = [];
    /// <summary>Gets safe registration metadata, not credential-resolution results.</summary>
    public IReadOnlyList<GitCredentialInfo> Credentials { get; private set; } = [];
    /// <summary>Gets accepted resolver precedence in service-provided order.</summary>
    public IReadOnlyList<GitCredentialResolverInfo> Resolvers { get; private set; } = [];
    /// <summary>Gets the exact selected ID; filters never change it.</summary>
    public string? SelectedId { get; private set; }
    /// <summary>Gets the selected accepted snapshot even when inventory filters hide its row.</summary>
    public GitRepositoryStatus? SelectedRepository => Repositories.FirstOrDefault(x => SameId(x.Id, SelectedId));
    /// <summary>Gets configured manual admission policy; the facade remains authoritative.</summary>
    public bool ManualSyncEnabled => options.Value.IsSyncTriggerEnabled(GitSyncTrigger.Manual);
    /// <summary>Gets whether an explicit evidence refresh is admitted.</summary>
    public bool IsLoading { get; private set; }
    /// <summary>Gets whether this visit has permanently stopped admitting work or publishing changes.</summary>
    public bool IsRetired { get; private set; }
    /// <summary>Gets refresh or confirmation/command admission, including a reserved native confirmation.</summary>
    public bool IsBusy => IsLoading || Operation is not null;
    /// <summary>Gets the most recent repository read failure; accepted snapshots are retained.</summary>
    public GitWorkspaceFailure? RepositoryFailure { get; private set; }
    /// <summary>Gets the most recent metadata read failure; it never replaces repository read evidence.</summary>
    public GitWorkspaceFailure? MetadataFailure { get; private set; }
    /// <summary>Gets local receipt time of the accepted repository read, distinct from successful sync time.</summary>
    public DateTimeOffset? RepositoriesObservedAt { get; private set; }
    /// <summary>Gets local receipt time of the accepted credential/resolver pair.</summary>
    public DateTimeOffset? MetadataObservedAt { get; private set; }
    /// <summary>Gets a subscriber failure for owner diagnostics; it does not change backend command outcomes.</summary>
    public Exception? NotificationError { get; private set; }
    /// <summary>Gets current confirmation/command intent; this is not a fabricated backend progress stage.</summary>
    public GitCommandScope? Operation { get; private set; }
    /// <summary>Gets the last completed admitted command outcome; native cancellation creates no outcome.</summary>
    public GitCommandOutcome? Outcome { get; private set; }

    /// <summary>Selects a registered accepted ID using service-compatible case-insensitive identity.</summary>
    public void Select(string id)
    {
        if (IsRetired) return;
        var repository = Repositories.FirstOrDefault(x => SameId(x.Id, id));
        if (repository is null) return;
        SelectedId = repository.Id;
        Notify();
    }

    /// <summary>Refreshes accepted evidence. Failed reads retain the previous accepted data.</summary>
    public Task RefreshAsync(bool includeMetadata = true)
    {
        if (IsRetired || IsBusy) return Task.CompletedTask;
        IsLoading = true;
        return Track(async () =>
        {
            try { Notify(); if (!IsRetired) await ReadAsync(includeMetadata); }
            finally { if (!IsRetired) { IsLoading = false; Notify(); } }
        });
    }

    /// <summary>Refreshes the safe credential/resolver pair without reading repository disk state.</summary>
    public Task RefreshMetadataAsync()
    {
        if (IsRetired || IsBusy) return Task.CompletedTask;
        IsLoading = true;
        return Track(async () =>
        {
            try { Notify(); if (!IsRetired) await ReadMetadataAsync(); }
            finally { if (!IsRetired) { IsLoading = false; Notify(); } }
        });
    }

    /// <summary>Reserves a command before the caller opens native confirmation. No mutation starts here.</summary>
    public bool TryBeginCommand(GitCommandKind kind, out GitCommandScope? scope)
    {
        scope = null;
        if (kind is not (GitCommandKind.Sync or GitCommandKind.SyncAll or GitCommandKind.DeleteLocal)) return false;
        if (IsRetired || IsBusy || (kind != GitCommandKind.DeleteLocal && !ManualSyncEnabled)) return false;
        IReadOnlyList<GitRepositoryStatus> targets = kind == GitCommandKind.SyncAll ? Repositories :
            SelectedRepository is { } selected ? new[] { selected } : [];
        if (targets.Count == 0) return false;
        scope = new GitCommandScope(++_commandVersion, kind, targets.Select(x => new GitCommandTarget(
            x.Id, x.ResolvedLocalPath, x.ConfiguredBranch, x.ResolvedBranch, x.IsDirty, x.IsAvailable)).ToArray());
        Operation = scope;
        Outcome = null;
        Notify();
        return true;
    }

    /// <summary>Releases an unexecuted reservation after native Cancel/Escape or confirmation failure.</summary>
    public void CancelCommand(GitCommandScope scope)
    {
        if (IsRetired || !ReferenceEquals(Operation, scope) || scope.HasStarted) return;
        Operation = null;
        Notify();
    }

    /// <summary>Executes the exact reserved confirmation scope once; tracks actual backend completion.</summary>
    public Task ExecuteAsync(GitCommandScope scope)
    {
        if (IsRetired || !ReferenceEquals(Operation, scope) || scope.HasStarted) return Task.CompletedTask;
        scope.HasStarted = true;
        return Track(async () =>
        {
            try
            {
                var mutation = MutateAsync(scope);
                try
                {
                    while (!mutation.IsCompleted && !IsRetired)
                    {
                        try
                        {
                            var delay = Task.Delay(TimeSpan.FromMilliseconds(400), _lifetime.Token);
                            if (await Task.WhenAny(mutation, delay) == mutation) break;
                            await delay;
                        }
                        catch (OperationCanceledException) when (IsRetired) { break; }
                        if (IsRetired) break;
                        await ReadAsync(false);
                    }
                }
                finally { await mutation; }
                var outcome = await mutation;
                if (IsRetired) return;
                Outcome = outcome;
                await ReadAsync(false);
            }
            finally { if (!IsRetired) { Operation = null; Notify(); } }
        });
    }

    private async Task<GitCommandOutcome> MutateAsync(GitCommandScope scope)
    {
        switch (scope.Kind)
        {
            case GitCommandKind.SyncAll:
                var all = await facade.SyncAllAsync();
                var results = all.Data;
                return all.IsFailure() || results is null
                    ? new(scope, all.Message ?? string.Empty, [], null) : new(scope, null, results, null);
            case GitCommandKind.DeleteLocal:
                var deletion = await facade.DeleteRepositoryAsync(scope.Targets[0].Id);
                var deleted = deletion.Data;
                return deletion.IsFailure() || deleted is null
                    ? new(scope, deletion.Message ?? string.Empty, [], null) : new(scope, null, [], deleted);
            default:
                var sync = await facade.SyncRepositoryAsync(scope.Targets[0].Id);
                var result = sync.Data;
                return sync.IsFailure() || result is null
                    ? new(scope, sync.Message ?? string.Empty, [], null) : new(scope, null, [result], null);
        }
    }

    private async Task ReadAsync(bool metadata)
    {
        var repositories = await facade.GetRepositoriesAsync();
        if (IsRetired) return;
        var values = repositories.Data;
        if (repositories.IsFailure() || values is null) RepositoryFailure = new(GitWorkspaceReadKind.Repositories, repositories.Message ?? string.Empty);
        else
        {
            Repositories = values.ToArray();
            SelectedId = Repositories.FirstOrDefault(x => SameId(x.Id, SelectedId))?.Id ?? Repositories.FirstOrDefault()?.Id;
            RepositoryFailure = null;
            RepositoriesObservedAt = DateTimeOffset.Now;
        }
        Notify();
        if (!metadata || IsRetired) return;
        await ReadMetadataAsync();
    }

    private async Task ReadMetadataAsync()
    {
        var credentials = await facade.GetCredentialsAsync();
        if (IsRetired) return;
        var credentialValues = credentials.Data;
        if (credentials.IsFailure() || credentialValues is null)
        { MetadataFailure = new(GitWorkspaceReadKind.Credentials, credentials.Message ?? string.Empty); Notify(); return; }
        var resolvers = await facade.GetCredentialResolversAsync();
        if (IsRetired) return;
        var resolverValues = resolvers.Data;
        if (resolvers.IsFailure() || resolverValues is null)
        { MetadataFailure = new(GitWorkspaceReadKind.Resolvers, resolvers.Message ?? string.Empty); Notify(); return; }
        Credentials = credentialValues.ToArray();
        Resolvers = resolverValues.ToArray();
        MetadataFailure = null;
        MetadataObservedAt = DateTimeOffset.Now;
        Notify();
    }

    // Reserve the drainable task before invoking any producer that may complete synchronously/reenter.
    private Task Track(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _users.Add(completion.Task);
        _ = CompleteAsync();
        return completion.Task;
        async Task CompleteAsync()
        {
            Exception? failure = null;
            try { await action(); }
            catch (Exception exception) { failure = exception; }
            finally { _users.Remove(completion.Task); }
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
    }

    private void Notify()
    {
        if (IsRetired || Changed is not { } subscribers) return;
        foreach (Action subscriber in subscribers.GetInvocationList())
        {
            if (IsRetired) break;
            try { subscriber(); }
            catch (Exception exception) { NotificationError = exception; }
        }
    }
    private static bool SameId(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>Idempotently stops publication/polling and joins all admitted tasks without disposing borrowed services.</summary>
    public ValueTask DisposeAsync()
    {
        if (_retirement is not null) return new(_retirement);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _retirement = completion.Task;
        IsRetired = true;
        Changed = null;
        _ = RetireAsync();
        return new(_retirement);
        async Task RetireAsync()
        {
            List<Exception> errors = [];
            try { _lifetime.Cancel(); } catch (Exception exception) { errors.Add(exception); }
            try { await Task.WhenAll(_users.ToArray()); } catch (Exception exception) { errors.Add(exception); }
            _lifetime.Dispose();
            if (errors.Count == 0) completion.TrySetResult();
            else completion.TrySetException(errors);
        }
    }
}

/// <summary>Supported deliberate UI actions; DeleteLocal is independent of manual-sync policy.</summary>
public enum GitCommandKind { Sync, SyncAll, DeleteLocal }
/// <summary>Identifies the actual facade read that failed.</summary>
public enum GitWorkspaceReadKind { Repositories, Credentials, Resolvers }
/// <summary>A read failure distinct from accepted repository LastError and command outcomes.</summary>
public sealed record GitWorkspaceFailure(GitWorkspaceReadKind Kind, string Detail);
/// <summary>Frozen confirmation evidence; it does not claim the disk remains unchanged before execution.</summary>
public sealed record GitCommandTarget(string Id, string ResolvedLocalPath, string? ConfiguredBranch,
    string? ResolvedBranch, bool IsDirty, bool IsAvailable);
/// <summary>An owner-issued, one-shot command reservation. The UI owns its native confirmation lifetime.</summary>
public sealed class GitCommandScope(long version, GitCommandKind kind, IReadOnlyList<GitCommandTarget> targets)
{
    /// <summary>Gets the visit-local monotonically increasing reservation identity.</summary>
    public long Version { get; } = version;
    /// <summary>Gets the confirmed action.</summary>
    public GitCommandKind Kind { get; } = kind;
    /// <summary>Gets frozen targets from the full accepted inventory, independent of filters.</summary>
    public IReadOnlyList<GitCommandTarget> Targets { get; } = Array.AsReadOnly(targets.ToArray());
    /// <summary>Gets whether this reservation has admitted its one backend producer.</summary>
    public bool HasStarted { get; internal set; }
}
/// <summary>Actual facade/backend outcome; envelope failure and individual result failure remain distinct.</summary>
public sealed record GitCommandOutcome(GitCommandScope Scope, string? FacadeError,
    IReadOnlyList<GitSyncResult> SyncResults, GitRepositoryDeleteResult? DeleteResult);
