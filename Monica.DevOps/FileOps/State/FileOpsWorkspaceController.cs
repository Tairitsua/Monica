using Monica.DevOps.FileOps.Facades;
using Monica.DevOps.FileOps.Models;

namespace Monica.DevOps.FileOps.State;

/// <summary>Owns one FileOps visit's accepted evidence, draft and serialized facade operations.</summary>
/// <remarks>Create this controller in the routed component; the facade is borrowed, never disposed.
/// State access and command invocation belong to that visit's renderer execution context. Admission and
/// retirement reserve completions atomically, but invoke no external code under their lock. Reads use
/// a cached lifetime token. Started mutations are deliberately uncancelled and drained during retirement.
/// Native owners must dismiss and drain their dialogs/imports/invocations through retireCallbacks before
/// waiting for admitted work. Notifications return the actual renderer-dispatch task and are awaited.
/// Never await this controller's retirement from inside its own admitted callback.</remarks>
public sealed partial class FileOpsWorkspaceController : IAsyncDisposable
{
    private readonly FileOpsFacade _facade;
    private readonly Func<Task>? _changed;
    private readonly Func<Task>? _retireCallbacks;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly HashSet<string> _selected = new(PathComparer);
    private TaskCompletionSource<FileOpsOperationDisposition>? _active;
    private TaskCompletionSource? _retirement;
    private bool _retired;
    private bool _busy;
    private bool _scopeReadFailed;
    private FileOpsRuntimeConfig? _config;
    private FileOpsBrowseResult? _browse;
    private FileOpsEntrySummary? _entry;
    private FileOpsFileTextContent? _file;
    private FileOpsInspectionAttempt? _inspectionAttempt;
    private string _draft = string.Empty;
    private string _baseline = string.Empty;
    private string _proposedPath = string.Empty;
    private long _inspectionId;
    private long _draftRevision;

    /// <summary>Creates a visit owner with optional awaited renderer and native-resource retirement callbacks.</summary>
    public FileOpsWorkspaceController(FileOpsFacade facade, Func<Task>? changed = null, Func<Task>? retireCallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(facade);
        _facade = facade;
        _changed = changed;
        _retireCallbacks = retireCallbacks;
        _token = _lifetime.Token;
    }

    /// <summary>Gets a detached accepted host-policy snapshot.</summary>
    public FileOpsRuntimeConfig? RuntimeConfig => _config?.Clone();
    /// <summary>Gets detached accepted directory evidence, retained after a failed browse.</summary>
    public FileOpsBrowseResult? Browse => _browse is null ? null : FileOpsWorkspaceSnapshots.Copy(_browse);
    /// <summary>Gets the separately inspected entry; batch checks do not change it.</summary>
    public FileOpsEntrySummary? InspectedEntry => _entry is null ? null : FileOpsWorkspaceSnapshots.Copy(_entry);
    /// <summary>Gets retained real disk metadata and content, independently of the editor draft.</summary>
    public FileOpsFileTextContent? InspectedFile => _file is null ? null : FileOpsWorkspaceSnapshots.Copy(_file);
    /// <summary>Gets a detached failed-preview target and read outcome without replacing an accepted draft.</summary>
    public FileOpsInspectionAttempt? InspectionAttempt => _inspectionAttempt is null ? null
        : new(_inspectionAttempt.FullPath, _inspectionAttempt.Name,
            _inspectionAttempt.Entry is null ? null : FileOpsWorkspaceSnapshots.Copy(_inspectionAttempt.Entry), _inspectionAttempt.Outcome);
    /// <summary>Gets the current user text, which may be newer than an admitted save.</summary>
    public string DraftText => _draft;
    /// <summary>Gets the baseline against which ordinal draft equality is computed.</summary>
    public string BaselineText => _baseline;
    /// <summary>Gets the monotonically increasing user-draft revision.</summary>
    public long DraftRevision => _draftRevision;
    /// <summary>Gets whether the user text differs from the retained accepted or persisted baseline.</summary>
    public bool IsDirty => _file is not null && !string.Equals(_draft, _baseline, StringComparison.Ordinal);
    /// <summary>Gets whether confirmation, production or reconciliation is in progress.</summary>
    /// <remarks>The settled notification observes false while the operation's admission/drain lease remains held.</remarks>
    public bool IsBusy => _busy;
    /// <summary>Gets whether this visit has stopped new admission and presentation.</summary>
    public bool IsRetired => Volatile.Read(ref _retired);
    /// <summary>Gets whether file metadata is retained after a failed read or pending reconciliation.</summary>
    public bool MetadataStale { get; private set; }
    /// <summary>Gets whether real disk text differs from the baseline; this is not conflict prevention.</summary>
    public bool DiskContentDiffers => !MetadataStale && _file is not null && !string.Equals(_file.Content, _baseline, StringComparison.Ordinal);
    /// <summary>Gets whether directory evidence is retained after a failed read or policy invalidation.</summary>
    public bool BrowseStale { get; private set; }
    /// <summary>Gets or edits the proposed navigation value, separate from accepted directory identity.</summary>
    public string ProposedPath { get => _proposedPath; set { if (!IsRetired) _proposedPath = value ?? string.Empty; } }
    /// <summary>Gets the local sort key, initially Name.</summary>
    public FileOpsSortField SortField { get; private set; }
    /// <summary>Gets the local sort direction, initially ascending.</summary>
    public bool SortDescending { get; private set; }
    /// <summary>Gets the latest actual command outcome, independent of subsequent evidence reads.</summary>
    public FileOpsOutcome? LastCommand { get; private set; }
    /// <summary>Gets the latest actual facade read outcome.</summary>
    public FileOpsOutcome? LastRead { get; private set; }
    /// <summary>Gets the remembered-directory failure retained when initialization falls back to startup.</summary>
    public FileOpsOutcome? NavigationNotice { get; private set; }

    /// <summary>Gets when the accepted directory observation was taken.</summary>
    public DateTimeOffset? DirectoryObservedAt { get; private set; }

    /// <summary>Gets when the accepted host policy was read.</summary>
    public DateTimeOffset? PolicyObservedAt { get; private set; }
    /// <summary>Gets a detached checked-path list using platform path identity.</summary>
    public IReadOnlyList<string> SelectedPaths => _selected.ToArray();
    /// <summary>Gets detached sorted accepted entries; directories always precede files.</summary>
    public IReadOnlyList<FileOpsEntrySummary> SortedEntries => SortEntries().Select(FileOpsWorkspaceSnapshots.Copy).ToArray();

    /// <summary>Updates the current editor draft; newer edits may survive an already-admitted save.</summary>
    public void SetDraft(string content)
    {
        if (IsRetired || _file is null || string.Equals(_draft, content, StringComparison.Ordinal)) return;
        _draft = content ?? string.Empty;
        _draftRevision++;
    }

    /// <summary>Sets local sorting without changing checked paths or inspection.</summary>
    public void SetSort(FileOpsSortField field, bool descending)
    { if (!IsRetired) { SortField = field; SortDescending = descending; } }

    /// <summary>Checks only an accepted displayed path; pending operations reserve selection transitions.</summary>
    public void ToggleEntrySelection(FileOpsEntrySummary entry, bool selected)
    {
        if (IsRetired || IsBusy || _browse?.Entries.Any(value => PathsEqual(value.FullPath, entry.FullPath)) != true) return;
        if (selected) _selected.Add(entry.FullPath); else _selected.Remove(entry.FullPath);
    }

    /// <summary>Checks or unchecks only the supplied visible subset of the accepted directory.</summary>
    public void SelectVisible(IEnumerable<FileOpsEntrySummary> entries, bool selected)
    {
        if (IsRetired || IsBusy || _browse is null) return;

        var visible = entries.Select(entry => entry.FullPath).ToHashSet(PathComparer);
        foreach (var entry in _browse.Entries.Where(entry => visible.Contains(entry.FullPath)))
        {
            if (selected) _selected.Add(entry.FullPath);
            else _selected.Remove(entry.FullPath);
        }
    }

    /// <summary>Checks or unchecks the displayed accepted subset, including a truncated directory's subset.</summary>
    public void SelectAll(bool selected)
    {
        if (IsRetired || IsBusy) return;
        _selected.Clear();
        if (selected && _browse is not null) _selected.UnionWith(_browse.Entries.Select(entry => entry.FullPath));
    }

    /// <summary>Clears batch selection without replacing inspection or draft text.</summary>
    public void ClearSelection() { if (!IsRetired && !IsBusy) _selected.Clear(); }
    /// <summary>Tests a checked path using the current platform's path comparison.</summary>
    public bool IsSelected(FileOpsEntrySummary entry) => _selected.Contains(entry.FullPath);

    /// <summary>Loads authoritative policy first, optionally browsing a remembered directory or startup.</summary>
    /// <remarks>Policy-only initialization permits evidence reads without browsing. An unavailable remembered
    /// directory keeps its typed failure notice while attempting the normalized actual startup directory.</remarks>
    public Task<FileOpsOperationDisposition> InitializeAsync(bool browseWorkspace = true, string? rememberedDirectory = null) => Admit(() => async () =>
    {
        if (!await ReadPolicyAsync() || !browseWorkspace || IsRetired) return Disposition;
        var requested = string.IsNullOrWhiteSpace(rememberedDirectory) ? _config!.StartupPath : rememberedDirectory;
        if (!await BrowseCoreAsync(requested) && !IsRetired && !PathsEqual(requested, _config!.StartupPath))
        {
            NavigationNotice = LastRead;
            await BrowseCoreAsync(_config!.StartupPath);
        }
        return Disposition;
    });

    /// <summary>Refreshes real policy/directory/file metadata while preserving an unrelated dirty draft.</summary>
    public Task<FileOpsOperationDisposition> RefreshAsync() => Admit(() =>
    {
        var origin = _browse?.CurrentPath;
        return async () => { await RefreshCoreAsync(origin); return Disposition; };
    });

    /// <summary>Browses a captured proposed path; dirty inspection requires explicit discard before replacement.</summary>
    public Task<FileOpsOperationDisposition> NavigateAsync(string path, FileOpsDiscardConfirmation? discard = null) => Admit(() =>
    {
        var target = path.Trim();
        var stamp = CaptureDraft();
        var replaces = _browse is null || !PathsEqual(_browse.CurrentPath, target);
        return async () =>
        {
            _proposedPath = target;
            if (replaces && !await ConfirmDiscardAsync(stamp, FileOpsDiscardReason.Navigate, target, discard)) return DispositionOrCancel;
            if (IsRetired) return FileOpsOperationDisposition.Retired;
            var accepted = await BrowseCoreAsync(target, replaces, stamp);
            if (!accepted && replaces && !Matches(stamp)) return DispositionOrCancel;
            if (accepted && !replaces) await RefreshInspectionAsync();
            return Disposition;
        };
    });

    /// <summary>Navigates to the accepted parent captured before any discard callback.</summary>
    public Task<FileOpsOperationDisposition> NavigateUpAsync(FileOpsDiscardConfirmation? discard = null) =>
        _browse?.ParentPath is { } parent ? NavigateAsync(parent, discard) : Task.FromResult(RejectedDisposition);

    /// <summary>Inspects the captured entry or browses its directory, preserving the old draft on failure.</summary>
    public Task<FileOpsOperationDisposition> OpenEntryAsync(FileOpsEntrySummary entry, FileOpsDiscardConfirmation? discard = null)
    {
        if (entry.IsDirectory) return NavigateAsync(entry.FullPath, discard);
        return Admit(() =>
        {
            var captured = FileOpsWorkspaceSnapshots.Copy(entry);
            var stamp = CaptureDraft();
            return async () =>
            {
                if (_file is not null && PathsEqual(_file.FullPath, captured.FullPath))
                { _inspectionAttempt = null; return Disposition; }
                if (!await ConfirmDiscardAsync(stamp, FileOpsDiscardReason.OpenFile, captured.FullPath, discard)) return DispositionOrCancel;
                if (!IsRetired && !await ReadInspectionAsync(captured.FullPath, replace: true, stamp, captured) && !Matches(stamp)) return DispositionOrCancel;
                return Disposition;
            };
        });
    }

    /// <summary>Directly inspects an exact text path, including a remembered path outside the displayed cap.</summary>
    /// <remarks>The actual facade read supplies accepted text metadata. A failed attempt retains exact path
    /// identity and any prior accepted draft; absent real row metadata, no entry capabilities are invented.</remarks>
    public Task<FileOpsOperationDisposition> OpenTextPathAsync(string path, FileOpsDiscardConfirmation? discard = null) => Admit(() =>
    {
        var target = path;
        var stamp = CaptureDraft();
        var known = _browse?.Entries.FirstOrDefault(entry => PathsEqual(entry.FullPath, target));
        var captured = known is null ? null : FileOpsWorkspaceSnapshots.Copy(known);
        return async () =>
        {
            if (_file is not null && PathsEqual(_file.FullPath, target))
            { _inspectionAttempt = null; return Disposition; }
            if (!await ConfirmDiscardAsync(stamp, FileOpsDiscardReason.OpenFile, target, discard)) return DispositionOrCancel;
            var entry = captured ?? (await _facade.ObserveFileAsync(target, _token)).Data;
            if (!IsRetired && !await ReadInspectionAsync(target, replace: true, stamp, entry) && !Matches(stamp)) return DispositionOrCancel;
            return Disposition;
        };
    });

    /// <summary>Refreshes the inspected file's disk evidence without replacing a dirty draft.</summary>
    public Task<FileOpsOperationDisposition> RefreshInspectionEvidenceAsync() => Admit(() => async () =>
    {
        await RefreshInspectionAsync();
        return Disposition;
    });

    /// <summary>Explicitly rereads the captured file, requiring discard before replacing a dirty draft.</summary>
    public Task<FileOpsOperationDisposition> ReloadAsync(FileOpsDiscardConfirmation? discard = null) => Admit(() =>
    {
        var file = _file;
        var stamp = CaptureDraft();
        return async () =>
        {
            if (file is null) return FileOpsOperationDisposition.Rejected;
            if (!await ConfirmDiscardAsync(stamp, FileOpsDiscardReason.Reload, file.FullPath, discard)) return DispositionOrCancel;
            if (!IsRetired && !await ReadInspectionAsync(file.FullPath, replace: true, stamp, _entry) && !Matches(stamp)) return DispositionOrCancel;
            return Disposition;
        };
    });

    /// <summary>Confirms route departure without clearing the draft; refusal or overlap prevents departure.</summary>
    public Task<FileOpsOperationDisposition> ConfirmLeaveAsync(FileOpsDiscardConfirmation? discard = null) => Admit(() =>
    {
        var stamp = CaptureDraft();
        return async () => await ConfirmDiscardAsync(stamp, FileOpsDiscardReason.Leave, null, discard) ? Disposition : DispositionOrCancel;
    });

    /// <summary>Closes inspection only after an explicit decision for the still-current dirty draft.</summary>
    public Task<FileOpsOperationDisposition> CloseInspectorAsync(FileOpsDiscardConfirmation? discard = null) => Admit(() =>
    {
        var stamp = CaptureDraft();
        return async () =>
        {
            if (!await ConfirmDiscardAsync(stamp, FileOpsDiscardReason.CloseInspector, null, discard)) return DispositionOrCancel;
            if (!IsRetired && Matches(stamp)) ClearInspection();
            return Disposition;
        };
    });

    private async Task<bool> ReadPolicyAsync()
    {
        if (IsRetired) return false;
        var result = await _facade.GetRuntimeConfigAsync(_token);
        if (IsRetired) return false;
        if (!RecordRead(FileOpsOutcome.From(FileOpsOperationKind.LoadPolicy, result, result.Data is not null))) return false;
        _config = result.Data!.Clone();
        PolicyObservedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private async Task<bool> BrowseCoreAsync(string? path, bool replaceInspection = false, DraftStamp? stamp = null)
    {
        if (IsRetired) return false;
        var result = await _facade.BrowseAsync(path, _token);
        if (IsRetired) return false;
        if (!RecordRead(FileOpsOutcome.From(FileOpsOperationKind.Browse, result, result.Data is not null)))
        { BrowseStale = _browse is not null; return false; }
        if (replaceInspection && stamp is not null && !Matches(stamp)) return false;
        _browse = FileOpsWorkspaceSnapshots.Copy(result.Data!);
        DirectoryObservedAt = DateTimeOffset.UtcNow;
        _proposedPath = _browse.CurrentPath;
        BrowseStale = false;
        var accepted = _browse.Entries.Select(entry => entry.FullPath).ToHashSet(PathComparer);
        _selected.RemoveWhere(path => !accepted.Contains(path));
        if (replaceInspection) ClearInspection();
        else if (_entry is not null && _browse.Entries.FirstOrDefault(entry => PathsEqual(entry.FullPath, _entry.FullPath)) is { } refreshed)
            _entry = FileOpsWorkspaceSnapshots.Copy(refreshed);
        return true;
    }

    private async Task<bool> ReadInspectionAsync(string path, bool replace, DraftStamp stamp, FileOpsEntrySummary? entry = null, bool keepSavedBaseline = false)
    {
        if (IsRetired) return false;
        var result = await _facade.ReadTextFileAsync(path, _token);
        if (IsRetired) return false;
        var outcome = FileOpsOutcome.From(FileOpsOperationKind.ReadText, result, result.Data is not null);
        if (!RecordRead(outcome))
        {
            if (_file is not null && PathsEqual(_file.FullPath, path)) MetadataStale = true;
            if (replace && Matches(stamp))
            {
                var evidence = entry is null ? null : FileOpsWorkspaceSnapshots.Copy(entry);
                _inspectionAttempt = new(path, Path.GetFileName(path), evidence, outcome);
                // A requested identity alone supplies no entry metadata or action capabilities.
                if (_file is null) _entry = evidence is null ? null : FileOpsWorkspaceSnapshots.Copy(evidence);
            }
            return false;
        }
        if (replace)
        {
            if (!Matches(stamp)) return false;
            _inspectionId++;
            _draftRevision++;
            _entry = entry is null ? null : FileOpsWorkspaceSnapshots.Copy(entry);
            _file = FileOpsWorkspaceSnapshots.Copy(result.Data!);
            _baseline = _draft = _file.Content;
            _inspectionAttempt = null;
        }
        else if (_inspectionId == stamp.InspectionId && _file is not null && PathsEqual(_file.FullPath, path))
        {
            var acceptText = !keepSavedBaseline && !IsDirty && Matches(stamp);
            _file = FileOpsWorkspaceSnapshots.Copy(result.Data!);
            if (acceptText) _baseline = _draft = _file.Content;
            if (_inspectionAttempt is not null && PathsEqual(_inspectionAttempt.FullPath, path)) _inspectionAttempt = null;
        }
        else return false;
        MetadataStale = false;
        return true;
    }

    /// <summary>Reads current host policy independently of the directory and its checked paths.</summary>
    public Task<FileOpsOperationDisposition> RefreshPolicyAsync() => Admit(() => async () =>
    {
        await ReadPolicyAsync();
        return Disposition;
    });

    private async Task RefreshCoreAsync(string? origin)
    {
        await ReadPolicyAsync();
        if (!IsRetired && (origin ?? _config?.StartupPath) is { } path) await BrowseCoreAsync(path);
        if (!IsRetired) await RefreshInspectionAsync();
    }

    private Task RefreshInspectionAsync() => _file is { } file
        ? ReadInspectionAsync(file.FullPath, replace: false, CaptureDraft()) : Task.CompletedTask;

    private bool RecordRead(FileOpsOutcome outcome)
    {
        // A later successful metadata read cannot hide a failure earlier in the same refresh scope.
        if (!_scopeReadFailed || !outcome.Succeeded) LastRead = outcome;
        _scopeReadFailed |= !outcome.Succeeded;
        return outcome.Succeeded;
    }

    private DraftStamp CaptureDraft() => new(_inspectionId, _draftRevision, _file?.FullPath, IsDirty);
    private bool Matches(DraftStamp stamp) => stamp.InspectionId == _inspectionId && stamp.Revision == _draftRevision;
    private async Task<bool> ConfirmDiscardAsync(DraftStamp stamp, FileOpsDiscardReason reason, string? destination, FileOpsDiscardConfirmation? callback)
    {
        if (IsRetired) return false;
        if (!stamp.Dirty) return Matches(stamp);
        if (callback is null || stamp.Path is null) return false;
        var affirmed = await callback(new(reason, stamp.Path, destination, stamp.Revision), _token);
        return affirmed && !IsRetired && Matches(stamp);
    }

    private void ClearInspection()
    {
        _inspectionId++;
        _draftRevision++;
        _entry = null; _file = null; _inspectionAttempt = null; _baseline = _draft = string.Empty; MetadataStale = false;
    }

    private IOrderedEnumerable<FileOpsEntrySummary> SortEntries()
    {
        var directories = (_browse?.Entries ?? []).OrderByDescending(entry => entry.IsDirectory);
        return (SortField, SortDescending) switch
        {
            (FileOpsSortField.Name, false) => directories.ThenBy(entry => entry.Name, PathComparer),
            (FileOpsSortField.Name, true) => directories.ThenByDescending(entry => entry.Name, PathComparer),
            (FileOpsSortField.Type, false) => directories.ThenBy(entry => entry.IsDirectory ? "" : entry.Extension, PathComparer).ThenBy(entry => entry.Name, PathComparer),
            (FileOpsSortField.Type, true) => directories.ThenByDescending(entry => entry.IsDirectory ? "" : entry.Extension, PathComparer).ThenBy(entry => entry.Name, PathComparer),
            (FileOpsSortField.Size, false) => directories.ThenBy(entry => entry.SizeBytes ?? -1).ThenBy(entry => entry.Name, PathComparer),
            (FileOpsSortField.Size, true) => directories.ThenByDescending(entry => entry.SizeBytes ?? -1).ThenBy(entry => entry.Name, PathComparer),
            (FileOpsSortField.Modified, false) => directories.ThenBy(entry => entry.LastModifiedAt).ThenBy(entry => entry.Name, PathComparer),
            _ => directories.ThenByDescending(entry => entry.LastModifiedAt).ThenBy(entry => entry.Name, PathComparer)
        };
    }

    private Task<FileOpsOperationDisposition> Admit(Func<Func<Task<FileOpsOperationDisposition>>> capture)
    {
        TaskCompletionSource<FileOpsOperationDisposition> completion;
        Func<Task<FileOpsOperationDisposition>> work;
        lock (_gate)
        {
            if (_retired || _active is not null) return Task.FromResult(RejectedDisposition);
            work = capture();
            completion = _active = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _busy = true;
            _scopeReadFailed = false;
        }
        // The reservation already exists when external code can re-enter admission or retirement.
        _ = CompleteScopeAsync(completion, work);
        return completion.Task;
    }

    private async Task CompleteScopeAsync(TaskCompletionSource<FileOpsOperationDisposition> completion, Func<Task<FileOpsOperationDisposition>> work)
    {
        var failures = new List<Exception>();
        var disposition = FileOpsOperationDisposition.Retired;
        try { await NotifyAsync(); if (!IsRetired) disposition = await work(); }
        catch (OperationCanceledException exception) when (_token.IsCancellationRequested && exception.CancellationToken == _token) { }
        catch (Exception exception) { failures.Add(exception); }
        _busy = false;
        // Publish settled state while retaining the admission/drain lease through the actual callback.
        try { await NotifyAsync(); } catch (Exception exception) { failures.Add(exception); }
        lock (_gate) { if (ReferenceEquals(_active, completion)) _active = null; }
        if (failures.Count > 0) completion.TrySetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
        else completion.TrySetResult(IsRetired ? FileOpsOperationDisposition.Retired : disposition);
    }

    private Task NotifyAsync() => !IsRetired && _changed is not null ? _changed() : Task.CompletedTask;

    /// <summary>Stops admission/presentation once, initiates native retirement, and drains every actual admitted task.</summary>
    /// <remarks>Repeated calls share one completion. Unexpected callback/cleanup failures remain observable;
    /// failure of one owner never skips drainage of the other owners or cancellation-source disposal.</remarks>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task? operation;
        lock (_gate)
        {
            if (_retirement is not null) return new(_retirement.Task);
            _retired = true;
            completion = _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            operation = _active?.Task;
        }
        _ = RetireCoreAsync(completion, operation);
        return new(completion.Task);
    }

    private async Task RetireCoreAsync(TaskCompletionSource completion, Task? operation)
    {
        var failures = new List<Exception>();
        Task? callbacks = null;
        Task? cancellation = null;
        try { callbacks = _retireCallbacks?.Invoke(); } catch (Exception exception) { failures.Add(exception); }
        try { cancellation = _lifetime.CancelAsync(); } catch (Exception exception) { failures.Add(exception); }
        try
        {
            foreach (var task in new[] { callbacks, cancellation, operation })
            {
                if (task is null) continue;
                try { await task; }
                catch (OperationCanceledException exception) when (_token.IsCancellationRequested && exception.CancellationToken == _token) { }
                catch (Exception exception) { failures.Add(exception); }
            }
        }
        finally { _lifetime.Dispose(); }
        if (failures.Count > 0) completion.TrySetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
        else completion.TrySetResult();
    }

    private FileOpsOperationDisposition Disposition => IsRetired ? FileOpsOperationDisposition.Retired : FileOpsOperationDisposition.Completed;
    private FileOpsOperationDisposition DispositionOrCancel => IsRetired ? FileOpsOperationDisposition.Retired : FileOpsOperationDisposition.Cancelled;
    private FileOpsOperationDisposition RejectedDisposition => IsRetired ? FileOpsOperationDisposition.Retired : FileOpsOperationDisposition.Rejected;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool PathsEqual(string? left, string? right) => PathComparer.Equals(left, right);
    private sealed record DraftStamp(long InspectionId, long Revision, string? Path, bool Dirty);
}
