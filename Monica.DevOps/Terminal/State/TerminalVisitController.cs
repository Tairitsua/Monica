using Monica.Core.Results;
using Monica.DevOps.Terminal.Facades;
using Monica.DevOps.Terminal.Models;
using Monica.UI.Shell.Support;

namespace Monica.DevOps.Terminal.State;

/// <summary>Owns one routed visit's evidence, draft, short operations and presentation work.</summary>
/// <remarks>
/// Call state-changing methods on the supplied component dispatcher. The facade and browser storage are
/// borrowed. Retirement joins visit work, but neither cancels nor waits for a host-owned command.
/// </remarks>
internal sealed class TerminalVisitController : IAsyncDisposable
{
    private const string OWNER_STORAGE_KEY = "terminal:owner-id";
    private readonly TerminalFacade _facade;
    private readonly IBrowserStorage _storage;
    private readonly Func<Func<Task>, Task> _dispatcher;
    private readonly Func<Task> _publish;
    private readonly object _lifetimeLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly HashSet<Task> _users = [];
    private readonly TimeProvider _clock;
    private volatile bool _retired;
    private Task? _retirement;
    private Task? _poll;
    private string? _owner;
    private bool _reading;
    private long _evidenceGeneration;
    private long _draftGeneration;
    private long _focusVersion;
    private TerminalFocusIntent? _focusIntent;
    private (string SessionId, string CommandId, long? FocusDraftGeneration)? _ownedCommand;

    internal TerminalVisitController(TerminalFacade facade, IBrowserStorage storage,
        Func<Func<Task>, Task> dispatcher, Func<Task> publish, TimeProvider? timeProvider = null)
    {
        _facade = facade;
        _storage = storage;
        _dispatcher = dispatcher;
        _publish = publish;
        _clock = timeProvider ?? TimeProvider.System;
        _lifetimeToken = _lifetime.Token;
    }

    internal TerminalEnvironmentSnapshot? Environment { get; private set; }
    internal IReadOnlyList<TerminalSessionSnapshot> Sessions { get; private set; } = [];
    internal string? SelectedSessionId { get; private set; }
    internal TerminalSessionSnapshot? ActiveSession => Sessions.FirstOrDefault(x => x.Id == SelectedSessionId);
    internal string Draft { get; private set; } = string.Empty;
    internal bool IsInitialized { get; private set; }
    internal bool IsLoading { get; private set; }
    internal bool IsOperationPending { get; private set; }
    internal bool IsRetired => _retired;
    internal bool IsRunning => ActiveSession?.IsRunning == true;
    internal bool CanChangeSession => IsInitialized && !_retired && !IsLoading && !IsOperationPending && !IsRunning;
    internal bool CanRun => CanChangeSession && ActiveSession is not null && !string.IsNullOrWhiteSpace(Draft);
    internal bool CanCancel => IsInitialized && !_retired && !IsOperationPending && ActiveSession?.ActiveCommand is { CancellationRequested: false };
    internal string? InitializationFailure { get; private set; }
    internal BrowserStorageWriteFailureKind? OwnerStorageFailure { get; private set; }
    internal string? ReadFailure { get; private set; }
    internal string? OperationFailure { get; private set; }
    internal DateTimeOffset? ObservedAt { get; private set; }
    internal Exception? NotificationError { get; private set; }

    internal void SetDraft(string? value)
    {
        if (_retired) return;
        Draft = value ?? string.Empty;
        _draftGeneration++;
    }

    internal Task InitializeAsync()
    {
        if (_retired || IsInitialized || IsLoading) return Task.CompletedTask;
        IsLoading = true;
        InitializationFailure = null;
        OwnerStorageFailure = null;
        return Track(async () =>
        {
            try
            {
                await PublishAsync();
                if (_retired) return;
                var owner = await _storage.GetAsync<string?>(OWNER_STORAGE_KEY, null);
                if (_retired) return;
                if (owner is null || !Guid.TryParseExact(owner, "N", out _))
                {
                    owner = Guid.NewGuid().ToString("N");
                    var write = await _storage.TrySetAsync(OWNER_STORAGE_KEY, owner);
                    if (_retired) return;
                    if (!write.Succeeded)
                    {
                        OwnerStorageFailure = write.FailureKind;
                        InitializationFailure = write.FailureKind.ToString();
                        return;
                    }
                }

                _owner = owner;
                var environment = await _facade.GetEnvironmentAsync();
                if (_retired) return;
                var environmentError = environment;
                var environmentValue = environment.Data;
                if (environment.IsFailure() || environmentValue is null)
                {
                    InitializationFailure = environmentError.Message ?? string.Empty;
                    return;
                }
                Environment = environmentValue;
                if (!await ReadCoreAsync(initializing: true) || _retired) return;
                if (Sessions.Count == 0)
                {
                    var created = await _facade.CreateSessionAsync(owner);
                    if (_retired) return;
                    var createError = created;
                    var session = created.Data;
                    if (created.IsFailure() || session is null)
                    {
                        InitializationFailure = createError.Message ?? string.Empty;
                        return;
                    }
                    Sessions = [session];
                    SelectedSessionId = session.Id;
                    ObservedAt = _clock.GetUtcNow();
                }
                IsInitialized = true;
                StartPolling();
            }
            catch (Exception error)
            {
                if (_retired) throw;
                InitializationFailure = error.Message ?? string.Empty;
            }
            finally
            {
                if (!_retired)
                {
                    IsLoading = false;
                    await PublishAsync();
                }
            }
        });
    }

    internal Task RefreshAsync()
    {
        if (!IsInitialized || _retired || _reading) return Task.CompletedTask;
        return Track(async () =>
        {
            await ReadCoreAsync();
            await PublishAsync();
        });
    }

    internal Task CreateSessionAsync()
    {
        if (!TryReserveMutation(requireSession: false)) return Task.CompletedTask;
        var owner = _owner!;
        var directory = ActiveSession?.WorkingDirectory;
        return MutateAsync(async () =>
        {
            var result = await _facade.CreateSessionAsync(owner, new() { WorkingDirectory = directory });
            if (_retired) return;
            var error = result;
            var session = result.Data;
            if (result.IsFailure() || session is null) { OperationFailure = error.Message ?? string.Empty; return; }
            Sessions = new[] { session }.Concat(Sessions.Where(x => x.Id != session.Id)).ToArray();
            SelectedSessionId = session.Id;
            RequestFocus();
        });
    }

    internal Task CloseSessionAsync()
    {
        if (!TryReserveMutation()) return Task.CompletedTask;
        var owner = _owner!;
        var id = SelectedSessionId!;
        return MutateAsync(async () =>
        {
            var result = await _facade.CloseSessionAsync(owner, id);
            if (_retired) return;
            var error = result;
            if (result.IsFailure()) { OperationFailure = error.Message ?? string.Empty; return; }
            if (!await ReadCoreAsync() || _retired) return;
            if (Sessions.Count == 0)
            {
                var replacement = await _facade.CreateSessionAsync(owner);
                if (_retired) return;
                var createError = replacement;
                var session = replacement.Data;
                if (replacement.IsFailure() || session is null) { OperationFailure = createError.Message ?? string.Empty; return; }
                Sessions = [session];
                SelectedSessionId = session.Id;
            }
            RequestFocus();
        });
    }

    internal Task ClearOutputAsync()
    {
        if (!TryReserveMutation()) return Task.CompletedTask;
        var owner = _owner!;
        var id = SelectedSessionId!;
        return MutateAsync(async () =>
        {
            var result = await _facade.ClearOutputAsync(owner, id);
            if (_retired) return;
            var error = result;
            if (result.IsFailure()) { OperationFailure = error.Message ?? string.Empty; return; }
            await ReadCoreAsync();
            if (!_retired) RequestFocus();
        });
    }

    internal Task StartCommandAsync()
    {
        if (!CanRun || !TryReserveMutation()) return Task.CompletedTask;
        var owner = _owner!;
        var id = SelectedSessionId!;
        var command = Draft.Trim();
        var generation = _draftGeneration;
        return MutateAsync(async () =>
        {
            var result = await _facade.StartCommandAsync(owner, id, new() { Command = command });
            if (_retired) return;
            var error = result;
            var acceptance = result.Data;
            if (result.IsFailure() || acceptance is null) { OperationFailure = error.Message ?? string.Empty; return; }
            long? focusGeneration = null;
            if (_draftGeneration == generation)
            {
                SetDraft(string.Empty);
                focusGeneration = _draftGeneration;
            }
            _ownedCommand = (id, acceptance.CommandId, focusGeneration);
            AdoptSession(acceptance.Session);
            ObserveOwnedCompletion();
        });
    }

    internal Task CancelCommandAsync()
    {
        if (!CanCancel || ActiveSession?.ActiveCommand is not { } active) return Task.CompletedTask;
        if (!TryReserveMutation(allowRunning: true)) return Task.CompletedTask;
        var owner = _owner!;
        var id = SelectedSessionId!;
        var commandId = active.CommandId;
        return MutateAsync(async () =>
        {
            var result = await _facade.CancelCommandAsync(owner, id, commandId);
            if (_retired) return;
            var error = result;
            var session = result.Data;
            if (result.IsFailure() || session is null) { OperationFailure = error.Message ?? string.Empty; return; }
            AdoptSession(session);
            ObserveOwnedCompletion();
        });
    }

    internal Task SelectSessionAsync(string id)
    {
        if (!CanChangeSession || Sessions.All(x => x.Id != id)) return Task.CompletedTask;
        SelectedSessionId = id;
        _ownedCommand = null;
        RequestFocus();
        return Track(PublishAsync);
    }

    internal Task UseHistoryAsync(string command)
    {
        if (!CanChangeSession || ActiveSession?.History.Contains(command, StringComparer.Ordinal) != true)
            return Task.CompletedTask;
        SetDraft(command);
        RequestFocus();
        return Track(PublishAsync);
    }

    internal TerminalFocusIntent? TakeFocusIntent()
    {
        var intent = _focusIntent;
        _focusIntent = null;
        return intent is not null && IsFocusIntentCurrent(intent) ? intent : null;
    }

    internal bool IsFocusIntentCurrent(TerminalFocusIntent intent) =>
        CanChangeSession && _focusVersion == intent.Version && SelectedSessionId == intent.SessionId &&
        _draftGeneration == intent.DraftGeneration;

    /// <summary>Tracks the actual native focus or presentation producer until retirement.</summary>
    internal Task RunPresentationAsync(Func<Task> operation) => Track(async () =>
    {
        if (!_retired) await operation();
    });

    private bool TryReserveMutation(bool requireSession = true, bool allowRunning = false)
    {
        if (_retired || !IsInitialized || IsLoading || IsOperationPending || _owner is null ||
            (!allowRunning && IsRunning) || (requireSession && ActiveSession is null)) return false;
        IsOperationPending = true;
        OperationFailure = null;
        _evidenceGeneration++;
        _focusIntent = null;
        return true;
    }

    private Task MutateAsync(Func<Task> operation) => Track(async () =>
    {
        try
        {
            await PublishAsync();
            if (!_retired) await operation();
        }
        catch (Exception error)
        {
            if (_retired) throw;
            OperationFailure = error.Message ?? string.Empty;
        }
        finally
        {
            if (!_retired)
            {
                IsOperationPending = false;
                await PublishAsync();
            }
        }
    });

    private async Task<bool> ReadCoreAsync(bool initializing = false)
    {
        if (_retired || _owner is null || _reading) return false;
        _reading = true;
        var generation = _evidenceGeneration;
        var owner = _owner;
        try
        {
            var response = await _facade.GetSessionsAsync(owner);
            if (_retired || generation != _evidenceGeneration) return false;
            var error = response;
            var sessions = response.Data;
            if (response.IsFailure() || sessions is null)
            {
                if (initializing) InitializationFailure = error.Message ?? string.Empty;
                else ReadFailure = error.Message ?? string.Empty;
                return false;
            }
            Sessions = sessions.ToArray();
            SelectedSessionId = Sessions.FirstOrDefault(x => x.Id == SelectedSessionId)?.Id ?? Sessions.FirstOrDefault()?.Id;
            ObservedAt = _clock.GetUtcNow();
            ReadFailure = null;
            ObserveOwnedCompletion();
            return true;
        }
        catch (Exception error)
        {
            if (_retired) throw;
            if (generation == _evidenceGeneration)
            {
                if (initializing) InitializationFailure = error.Message ?? string.Empty;
                else ReadFailure = error.Message ?? string.Empty;
            }
            return false;
        }
        finally { _reading = false; }
    }

    private void AdoptSession(TerminalSessionSnapshot session)
    {
        Sessions = Sessions.Select(x => x.Id == session.Id ? session : x).ToArray();
        ObservedAt = _clock.GetUtcNow();
    }

    private void ObserveOwnedCompletion()
    {
        if (_ownedCommand is not { } owned) return;
        var session = Sessions.FirstOrDefault(x => x.Id == owned.SessionId);
        if (session?.ActiveCommand?.CommandId == owned.CommandId) return;
        _ownedCommand = null;
        if (session is { IsRunning: false } && SelectedSessionId == owned.SessionId &&
            owned.FocusDraftGeneration is { } focusGeneration && _draftGeneration == focusGeneration &&
            (session.LastResult?.CommandId == owned.CommandId || session.LastExecutionFailure?.CommandId == owned.CommandId))
            RequestFocus();
    }

    private void RequestFocus()
    {
        if (!_retired && SelectedSessionId is { } id)
            _focusIntent = new(++_focusVersion, id, _draftGeneration);
    }

    private void StartPolling()
    {
        if (_retired || _poll is not null) return;
        _poll = Track(async () =>
        {
            try
            {
                while (!_retired)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(IsRunning ? 350 : 1500), _clock, _lifetimeToken);
                    if (_retired) break;
                    await _dispatcher(async () =>
                    {
                        if (_retired) return;
                        await ReadCoreAsync();
                        await PublishAsync();
                    });
                }
            }
            catch (OperationCanceledException error) when (_retired && error.CancellationToken == _lifetimeToken) { }
            catch (Exception error)
            {
                NotificationError = error;
                if (!_retired) ReadFailure = error.Message ?? string.Empty;
            }
        });
    }

    private Task PublishAsync()
    {
        if (_retired) return Task.CompletedTask;
        return _dispatcher(async () =>
        {
            if (_retired) return;
            try { await _publish(); }
            catch (Exception error) { NotificationError = error; throw; }
        });
    }

    // Reserve completion before any external synchronous prefix can call back into retirement.
    private Task Track(Func<Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifetimeLock)
        {
            if (_retired) return Task.CompletedTask;
            _users.Add(completion.Task);
        }
        _ = CompleteAsync();
        return completion.Task;

        async Task CompleteAsync()
        {
            try { await operation(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { lock (_lifetimeLock) _users.Remove(completion.Task); }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] users;
        lock (_lifetimeLock)
        {
            if (_retirement is not null) return new(_retirement);
            _retired = true;
            _focusIntent = null;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = completion.Task;
            users = _users.ToArray();
        }
        _ = RetireAsync();
        return new(completion.Task);

        async Task RetireAsync()
        {
            List<Exception> failures = [];
            try { await _lifetime.CancelAsync(); }
            catch (Exception error) { failures.Add(error); }
            foreach (var user in users)
            {
                try { await user; }
                catch (Exception error) { failures.Add(error); }
            }
            _lifetime.Dispose();
            if (failures.Count == 0) completion.TrySetResult();
            else completion.TrySetException(new AggregateException(failures));
        }
    }
}

internal sealed record TerminalFocusIntent(long Version, string SessionId, long DraftGeneration);
