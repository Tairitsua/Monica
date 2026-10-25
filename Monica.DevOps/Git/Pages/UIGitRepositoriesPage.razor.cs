using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Monica.DevOps.Git.Components;
using Monica.DevOps.Git.Facades;
using Monica.DevOps.Git.Models;
using Monica.DevOps.Git.State;
using Monica.DevOps.Git.Support;
using Monica.DevOps.Localization;
using Monica.Modules;
using MudBlazor;

namespace Monica.DevOps.Git.Pages;

/// <summary>Inspects working copies and owns explicit native confirmation and selection sessions.</summary>
public partial class UIGitRepositoriesPage : IAsyncDisposable
{
    /// <summary>The existing repository administration destination.</summary>
    public const string PAGE_URL = "/git-repositories";
    [Inject] private GitFacade GitService { get; set; } = null!;
    [Inject] private IOptions<ModuleGitOption> Options { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private IStringLocalizer<GitResource> L { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private ILogger<UIGitRepositoriesPage> Logger { get; set; } = null!;

    private GitWorkspaceController _workspace = null!;
    private GitPresentationPrivacy _privacy = null!;
    private readonly HashSet<Task> _users = [];
    private readonly HashSet<GitRepositorySyncState> _states = [];
    private string _query = "";
    private bool _dirtyOnly, _catalogOpen, _retired;
    private Task? _retirement;
    private IDialogReference? _dialog;
    private Task _closing = Task.CompletedTask;
    private Task<IJSObjectReference>? _moduleImport;
    private Task<IJSObjectReference>? _sizingStart;
    private Task? _sizingStop;
    private bool _sizingAdmitted;
    private readonly string _visit = "git-" + Guid.NewGuid().ToString("N");
    private long _focusVersion;
    private FocusIntent? _focusIntent;
    private ElementReference _root;
    private MudTextField<string>? _search;
    private bool Busy => _workspace?.IsBusy == true;
    private string SelectedKey => Safe(_workspace?.SelectedId);
    private bool HasFilters => _states.Count > 0 || _dirtyOnly || !string.IsNullOrWhiteSpace(_query);
    private IEnumerable<GitRepositoryStatus> VisibleRepositories => _workspace.Repositories.Where(repository =>
        (_states.Count == 0 || _states.Contains(repository.State)) && (!_dirtyOnly || repository.IsDirty) &&
        (string.IsNullOrWhiteSpace(_query) || new[] { repository.Id, repository.Provider, repository.ConfiguredBranch,
                repository.ResolvedBranch, repository.RemoteUrl, repository.LocalPath, repository.ResolvedLocalPath }
            .Any(value => Safe(value).Contains(_query.Trim(), StringComparison.OrdinalIgnoreCase))));
    private string ResultCount => L["Workspace:Repositories:Results", VisibleRepositories.Count(), _workspace.Repositories.Count];
    private string CatalogEmptyText => L[_workspace.Repositories.Count > 0 ? "Workspace:Repositories:NoMatches"
        : _workspace.RepositoriesObservedAt is not null ? "GitDashboard:Messages:NoRepositories"
        : _workspace.IsLoading ? "Workspace:Repositories:Loading" : "Workspace:Repositories:NoAcceptedData"];

    protected override async Task OnInitializedAsync()
    {
        _privacy = new(Options.Value.CredentialRegistrations);
        _workspace = new(GitService, Options);
        _workspace.Changed += CaptureChanged;
        await Track(async () => { await _workspace.RefreshAsync(); if (!_retired) _catalogOpen = _workspace.SelectedRepository is null; });
    }
    private void CaptureChanged()
    {
        if (_retired) return;
        _ = ObserveRenderAsync(Track(() => InvokeAsync(() => { if (!_retired) StateHasChanged(); })));
    }
    private async Task ObserveRenderAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { Logger.LogError("Git workspace render dispatch failed: {Error}", Safe(exception.Message)); }
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_retired) return;
        var initializeSizing = !_sizingAdmitted;
        var intent = _focusIntent;
        if (!initializeSizing && intent is null) return;
        _sizingAdmitted = true;
        _focusIntent = null;
        await Track(async () =>
        {
            _moduleImport ??= JS.InvokeAsync<IJSObjectReference>("import", "./_content/Monica.DevOps/Git/git-catalog.js").AsTask();
            try
            {
                var module = await _moduleImport;
                if (_retired) return;
                if (initializeSizing)
                {
                    // Publish the reservation before a producer can reenter retirement.
                    var started = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _sizingStart = started.Task;
                    try { started.TrySetResult(await module.InvokeAsync<IJSObjectReference>("createCatalogSizer", _root, _visit)); }
                    catch (Exception exception) { started.TrySetException(exception); }
                    await started.Task;
                    if (_retired) { await StopSizingAsync(); return; }
                }
                if (!_retired && intent is not null && intent.Version == _focusVersion && intent.Key == SelectedKey)
                    await module.InvokeVoidAsync("reveal", _root, _visit, intent.Version, intent.Kind, intent.Key);
            }
            catch (JSDisconnectedException) { }
            catch (OperationCanceledException) when (_retired) { }
        });
    }
    private Task StopSizingAsync()
    {
        if (_sizingStop is not null) return _sizingStop;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sizingStop = completion.Task;
        _ = StopAsync();
        return completion.Task;
        async Task StopAsync()
        {
            try
            {
                if (_sizingStart is not null)
                {
                    var observer = await _sizingStart;
                    try { await observer.InvokeVoidAsync("dispose"); }
                    finally { await observer.DisposeAsync(); }
                }
                completion.TrySetResult();
            }
            catch (JSDisconnectedException) { completion.TrySetResult(); }
            catch (OperationCanceledException) when (_retired) { completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }
    }
    private void CancelFocus() { _focusVersion++; _focusIntent = null; }
    private void SetQuery(string? value) { CancelFocus(); _query = value ?? ""; }
    private void SetDirty(bool value) { CancelFocus(); _dirtyOnly = value; }
    private void ToggleState(GitRepositorySyncState state) { CancelFocus(); if (!_states.Remove(state)) _states.Add(state); }
    private async Task ClearFiltersAsync()
    {
        CancelFocus(); _query = ""; _dirtyOnly = false; _states.Clear();
        if (_search is not null) await _search.ClearAsync();
    }
    private void ToggleCatalog()
    {
        if (_retired) return;
        CancelFocus(); _catalogOpen = !_catalogOpen;
        if (_catalogOpen) _focusIntent = new(_focusVersion, "catalog", SelectedKey);
    }
    private void SelectRepository(string id)
    {
        if (_retired) return;
        _workspace.Select(id);
        if (!SameId(_workspace.SelectedId, id)) return;
        CancelFocus(); _catalogOpen = false;
        _focusIntent = new(_focusVersion, "detail", SelectedKey);
    }
    private Task RefreshAsync()
    {
        if (_retired || Busy) return Task.CompletedTask;
        CancelFocus();
        return Track(async () => { await _workspace.RefreshAsync(); if (!_retired && _workspace.SelectedRepository is null) _catalogOpen = true; });
    }
    private Task ConfirmAsync(GitCommandKind kind)
    {
        // Freeze command admission before rendering or starting the native dialog producer.
        if (_retired || !_workspace.TryBeginCommand(kind, out var scope) || scope is null) return Task.CompletedTask;
        if (_retired) { _workspace.CancelCommand(scope); return Task.CompletedTask; }
        CancelFocus();
        var targets = scope.Targets.Select(target => new GitOperationConfirmTarget(Safe(target.Id), Safe(target.ResolvedLocalPath),
            Safe(target.ConfiguredBranch), Safe(target.ResolvedBranch), target.IsDirty, target.IsAvailable,
            Safe(_workspace.Repositories.FirstOrDefault(repository => SameId(repository.Id, target.Id))?.RemoteUrl))).ToArray();
        return Track(async () =>
        {
            try
            {
                var reference = await DialogService.ShowAsync<GitOperationConfirmDialog>(ConfirmationTitle(kind), new DialogParameters
                {
                    [nameof(GitOperationConfirmDialog.Kind)] = kind,
                    [nameof(GitOperationConfirmDialog.Targets)] = targets
                }, new() { FullWidth = true, MaxWidth = MaxWidth.Medium, CloseOnEscapeKey = true });
                _dialog = reference;
                var resultTask = reference.Result;
                if (_retired) await Task.WhenAll(CloseDialogAsync(reference), resultTask);
                var result = await resultTask;
                await _closing;
                if (_retired || result is not { Canceled: false, Data: true }) return;
                await _workspace.ExecuteAsync(scope);
                if (!_retired && _workspace.Outcome is { } outcome)
                {
                    var failed = outcome.FacadeError is not null || outcome.SyncResults.Any(item => !item.Success) || outcome.DeleteResult?.Success == false;
                    Snackbar.Add(failed ? L["Workspace:Repositories:OperationFailed", OutcomeMessage(outcome)] : L["Workspace:Repositories:OperationSucceeded"], failed ? Severity.Error : Severity.Success);
                }
            }
            catch (Exception exception)
            {
                Logger.LogError("Git workspace command or confirmation failed: {Error}", Safe(exception.Message));
                if (!_retired) Snackbar.Add(L["Workspace:Repositories:OperationFailed", Safe(exception.Message)], Severity.Error);
            }
            finally { _dialog = null; if (!scope.HasStarted) _workspace.CancelCommand(scope); }
        });
    }
    private Task CloseDialogAsync(IDialogReference reference)
    {
        // Reserve completion before Close, whose Result continuation may resume inline.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _closing = completion.Task;
        _ = CloseAsync();
        return completion.Task;
        async Task CloseAsync()
        {
            try { await InvokeAsync(() => reference.Close(DialogResult.Cancel())); completion.TrySetResult(); }
            catch (Exception exception)
            {
                try { reference.Dismiss(DialogResult.Cancel()); }
                catch (Exception dismissFailure) { exception = new AggregateException(exception, dismissFailure); }
                completion.TrySetException(exception);
            }
        }
    }
    private Task Track(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _users.Add(completion.Task);
        _ = CompleteAsync();
        return completion.Task;
        async Task CompleteAsync()
        {
            try { await action(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { _users.Remove(completion.Task); }
        }
    }
    /// <summary>Stops publication, settles owned confirmation, and joins work without disposing borrowed services.</summary>
    public ValueTask DisposeAsync()
    {
        if (_retirement is not null) return new(_retirement);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _retirement = completion.Task;
        _retired = true; CancelFocus();
        if (_workspace is not null) _workspace.Changed -= CaptureChanged;
        _ = RetireAsync();
        return new(_retirement);
        async Task RetireAsync()
        {
            try
            {
                var close = _dialog is { } reference ? CloseDialogAsync(reference) : Task.CompletedTask;
                var controller = _workspace?.DisposeAsync().AsTask() ?? Task.CompletedTask;
                var sizing = StopSizingAsync();
                try { await Task.WhenAll(close, controller, sizing, Task.WhenAll(_users.ToArray())); }
                finally
                {
                    if (_moduleImport is not null)
                    {
                        try { await (await _moduleImport).DisposeAsync(); }
                        catch (JSDisconnectedException) { }
                        catch (OperationCanceledException) when (_retired) { }
                    }
                }
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }
    }
    private string Safe(string? value) => _privacy?.Redact(value) ?? "";
    private string Display(string? value) => GitWorkspacePresentation.Display(Safe(value), L);
    private string StateText(GitRepositorySyncState state) => GitWorkspacePresentation.StateText(state, L);
    private static Color StateColor(GitRepositorySyncState state) => GitWorkspacePresentation.StateColor(state);
    private string BoolText(bool value) => GitWorkspacePresentation.BoolText(value, L);
    private string DateText(DateTimeOffset? value) => value is null ? L["Workspace:Repositories:NeverSynced"] : GitWorkspacePresentation.FormatDate(value, L, UiTime);
    private string ConfirmationTitle(GitCommandKind kind) => L[kind switch
    {
        GitCommandKind.SyncAll => "Workspace:Confirm:SyncAllTitle",
        GitCommandKind.DeleteLocal => "Workspace:Confirm:DeleteTitle",
        _ => "Workspace:Confirm:SyncTitle"
    }];
    private string OutcomeMessage(GitCommandOutcome outcome) => Safe(outcome.FacadeError ?? string.Join("; ",
        outcome.SyncResults.Where(item => !item.Success).Select(item => item.Message).Append(outcome.DeleteResult?.Success == false ? outcome.DeleteResult.Message : "").Where(message => !string.IsNullOrWhiteSpace(message))));
    private static bool SameId(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private string CredentialHref(string id) => "/git-credentials?credential=" + Uri.EscapeDataString(Safe(id));
    private sealed record FocusIntent(long Version, string Kind, string Key);
}
