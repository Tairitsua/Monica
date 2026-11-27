using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Monica.DevOps.FileOps.Dialogs;
using Monica.DevOps.FileOps.Facades;
using Monica.DevOps.FileOps.Models;
using Monica.DevOps.FileOps.Pages;
using Monica.DevOps.FileOps.State;
using Monica.DevOps.FileOps.Support;
using Monica.DevOps.Localization;
using Monica.UI.Shell.Support;
using MudBlazor;

namespace Monica.DevOps.FileOps.Components;

/// <summary>Composes one Files route and owns its rendered dialogs and browser transfers.</summary>
public partial class FileOpsWorkspace
{
    [Inject] private FileOpsFacade Facade { get; set; } = null!;
    [Inject] private FileOpsNavigationState Session { get; set; } = null!;
    [Inject] private IDialogService Dialogs { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private IStringLocalizer<FileOpsResource> L { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private PageAccessEvaluator PageAccess { get; set; } = null!;

    /// <summary>Shows the sibling runtime-policy page in the shared Files visit.</summary>
    [Parameter] public bool IsPolicy { get; set; }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _actions = [];
    private FileOpsWorkspaceController _controller = null!;
    private FileOpsDialogOwner _dialogs = null!;
    private FileOpsInspector? _inspector;
    private FileOpsTransferInteropSession _transfer = null!;
    private FileOpsViewQuery _query = new();
    private FileOpsBrowseResult? _browse;
    private FileOpsRuntimeConfig? _config;
    private FileOpsInventoryView _inventory = new([], new());
    private FileOpsOutcome? _policyRead, _observedCommand;
    private string? _commandTarget, _pendingTarget;
    private string? _publishedUri, _approvedUri;
    private long _approvedDraft;
    private bool _retired, _initializing = true, _accessDenied, _policyWorking, _uploadOpen, _createActive;
    private bool _legacyRedirect;
    private Task? _disposal;

    private FileOpsPolicyDraft? PolicyDraft => Session.PolicyDraft;
    private string FilesHref => Session.FilesQuery.Href();
    private string? SelectedPath => _controller.InspectionAttempt?.FullPath ?? _controller.InspectedFile?.FullPath;
    private bool IsDirty => _controller.IsDirty || PolicyDraft?.IsDirty == true;
    private static DialogOptions DecisionOptions => new() { FullWidth = true, MaxWidth = MaxWidth.Small, CloseOnEscapeKey = true };

    protected override async Task OnInitializedAsync()
    {
        _controller = Session.Borrow(Facade);
        _dialogs = new(Dialogs, action => InvokeAsync(action));
        _transfer = new(JS);
        Session.Changed += OnControllerChangedAsync;
        Navigation.LocationChanged += OnLocationChanged;
        UpdateEvidence();

        await Track(async () =>
        {
            _accessDenied = !await CanAccessAsync(UIFileOpsPolicyPage.PAGE_URL);
            if (_retired) return;
            if (IsPolicy)
            {
                if (!_accessDenied)
                {
                    await ReadPolicyAsync();
                    if (!_retired && _browse is null && _config is { } current &&
                        await CanAccessAsync(UIFileOpsPage.PAGE_URL))
                    {
                        await _controller.NavigateAsync(current.StartupPath);
                    }
                }
            }
            else
            {
                var requested = FileOpsViewQuery.Read(new Uri(Navigation.Uri));
                _legacyRedirect = requested.LegacyPolicy && !_accessDenied;
                _query = Session.FilesQuery;
                if (!_legacyRedirect) await ApplyLocationAsync(requested with { LegacyPolicy = false });
            }

            _initializing = false;
            UpdateEvidence();
        });
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_retired && !_initializing && _legacyRedirect)
        {
            _legacyRedirect = false;
            Navigation.NavigateTo(UIFileOpsPolicyPage.PAGE_URL, replace: true);
        }
        return Task.CompletedTask;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        if (_retired || IsPolicy || !IsFilesRoute(args.Location)) return;
        if (string.Equals(_publishedUri, args.Location, StringComparison.Ordinal))
        {
            _publishedUri = null;
            return;
        }
        _ = InvokeAsync(() => Track(async () =>
        {
            await ApplyLocationAsync(FileOpsViewQuery.Read(new Uri(args.Location)));
            if (!_retired && string.Equals(Navigation.Uri, args.Location, StringComparison.Ordinal))
            {
                if (_query.LegacyPolicy && !_accessDenied)
                    Navigation.NavigateTo(UIFileOpsPolicyPage.PAGE_URL, replace: true);
                StateHasChanged();
            }
        }));
    }

    private async Task ApplyLocationAsync(FileOpsViewQuery requested)
    {
        if (_retired) return;
        var prior = _query;
        _query = requested;
        _controller.SetSort(requested.Sort, requested.Descending);

        FileOpsDiscardConfirmation discard = _approvedUri == Navigation.Uri && _approvedDraft == _controller.DraftRevision
            ? static (_, _) => Task.FromResult(true) : ConfirmDiscardAsync;
        _approvedUri = null;
        var target = requested.Path ?? _config?.StartupPath;
        var disposition = FileOpsOperationDisposition.Completed;
        if (_browse is null)
            disposition = await _controller.InitializeAsync(rememberedDirectory: requested.Path);
        else if (target is not null && !SamePath(target, _browse.CurrentPath))
            disposition = await _controller.NavigateAsync(target, discard);

        if (!_retired && disposition == FileOpsOperationDisposition.Completed && !SamePath(requested.File, SelectedPath))
        {
            if (requested.File is null)
                disposition = await _controller.CloseInspectorAsync(discard);
            else
                disposition = await InspectPathAsync(requested.File, discard);
        }

        if (_retired) return;
        if (disposition != FileOpsOperationDisposition.Completed)
        {
            Publish(prior, replace: true);
            return;
        }
        _query = Session.FilesQuery = requested with { File = SelectedPath, LegacyPolicy = false };
        UpdateEvidence();
    }

    private Task<FileOpsOperationDisposition> InspectPathAsync(string path, FileOpsDiscardConfirmation discard)
    {
        var entry = _browse?.Entries.FirstOrDefault(row => !row.IsDirectory && SamePath(row.FullPath, path));
        return entry is null ? _controller.OpenTextPathAsync(path, discard) : _controller.OpenEntryAsync(entry, discard);
    }

    private async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (_retired) return;
        var destination = Navigation.ToAbsoluteUri(context.TargetLocation);
        if (string.Equals(_publishedUri, destination.AbsoluteUri, StringComparison.Ordinal)) return;
        if (_controller.IsBusy || _policyWorking || _uploadOpen)
        {
            context.PreventNavigation();
            return;
        }

        if (SelectedPath is not null && _inspector is { } inspector && !await inspector.FlushAsync())
        {
            context.PreventNavigation();
            return;
        }

        if (IsFamilyRoute(destination.AbsoluteUri))
        {
            if (IsPolicy || !IsFilesRoute(destination.AbsoluteUri) || !_controller.IsDirty) return;
            var target = FileOpsViewQuery.Read(destination);
            if (SamePath(target.Path, _query.Path) && SamePath(target.File, SelectedPath)) return;
            var revision = _controller.DraftRevision;
            var result = await Track(() => _controller.ConfirmLeaveAsync(ConfirmDiscardAsync));
            if (result != FileOpsOperationDisposition.Completed || revision != _controller.DraftRevision)
                context.PreventNavigation();
            else
            {
                _approvedUri = destination.AbsoluteUri;
                _approvedDraft = revision;
            }
            return;
        }

        var policy = PolicyDraft;
        var policyRevision = policy?.Revision;
        await Track(async () =>
        {
            if (policy?.IsDirty == true && !await ConfirmPolicyDiscardAsync())
            {
                context.PreventNavigation();
                return;
            }
            var result = await _controller.ConfirmLeaveAsync(ConfirmDiscardAsync);
            if (_retired || result != FileOpsOperationDisposition.Completed || policy?.Revision != policyRevision)
                context.PreventNavigation();
        });
    }

    private void Publish(FileOpsViewQuery query, bool replace)
    {
        if (_retired) return;
        _query = Session.FilesQuery = query;
        UpdateEvidence();
        var href = query.Href();
        var uri = Navigation.ToAbsoluteUri(href).AbsoluteUri;
        if (!string.Equals(uri, Navigation.Uri, StringComparison.Ordinal))
        {
            _publishedUri = uri;
            Navigation.NavigateTo(href, replace: replace);
        }
    }

    private Task SetFiltersAsync(FileOpsViewQuery filters)
    {
        _controller.SetSort(filters.Sort, filters.Descending);
        Publish(filters, replace: true);
        return Task.CompletedTask;
    }

    private Task NavigateAsync(string path) => Track(async () =>
    {
        if (await _controller.NavigateAsync(path, ConfirmDiscardAsync) != FileOpsOperationDisposition.Completed || _retired) return;
        if (_controller.LastRead is { Succeeded: false, Kind: FileOpsOperationKind.Browse }) return;
        if (_browse is not null)
            Publish(_query with { Path = _browse.CurrentPath, File = SelectedPath, Capability = FileOpsCapability.All, Search = "" }, replace: false);
    });

    private Task OpenAsync(FileOpsEntrySummary entry) => entry.IsDirectory ? NavigateAsync(entry.FullPath) : OpenFileAsync(entry.FullPath);

    private Task OpenFileAsync(string path) => Track(async () =>
    {
        if (await InspectPathAsync(path, ConfirmDiscardAsync) == FileOpsOperationDisposition.Completed && !_retired)
            Publish(_query with { File = SelectedPath }, replace: false);
    });

    private Task CloseInspectorAsync() => Track(async () =>
    {
        if (await _controller.CloseInspectorAsync(ConfirmDiscardAsync) == FileOpsOperationDisposition.Completed && !_retired)
            Publish(_query with { File = null }, replace: false);
    });

    private Task RefreshAsync() => IsPolicy ? ReloadPolicyAsync() : Track(async () =>
    {
        if (_browse is null)
            await _controller.InitializeAsync(rememberedDirectory: _query.Path);
        else
            await _controller.RefreshAsync();
    });

    private void EditDraft(string draft) => _controller.SetDraft(draft);

    private Task SaveTextAsync() => CommandAsync(_controller.SaveAsync, SelectedPath);
    private Task ReloadTextAsync() => Track(async () =>
    {
        if (_controller.IsDirty) await _controller.RefreshInspectionEvidenceAsync();
        if (!_retired) await _controller.ReloadAsync(ConfirmDiscardAsync);
    });
    private Task DownloadAsync(string path) => CommandAsync(() => _controller.DownloadAsync(path, TransferAsync), path);
    private Task DownloadSelectionAsync() => CommandAsync(() => _controller.DownloadSelectedAsync("files.zip", TransferAsync), null);
    private Task DeleteAsync(FileOpsEntrySummary entry) => CommandAsync(() => _controller.DeleteEntryAsync(entry, ConfirmDeleteAsync), entry.FullPath);
    private Task DeleteSelectionAsync() => CommandAsync(() => _controller.DeleteSelectedAsync(ConfirmDeleteAsync), null);

    private Task<bool> TransferAsync(FileOpsDownloadDescriptor descriptor, CancellationToken token) =>
        _retired ? Task.FromResult(false) : _transfer.DownloadAsync(descriptor);

    private Task CommandAsync(Func<Task<FileOpsOperationDisposition>> command, string? target) => Track(async () =>
    {
        if (_controller.IsBusy) return;
        _pendingTarget = target;
        try
        {
            await command();
            if (!_retired && !IsPolicy && !SamePath(_query.File, SelectedPath))
                Publish(_query with { File = SelectedPath }, replace: true);
        }
        finally { _pendingTarget = null; }
    });

    private Task CreateAsync() => Track(async () =>
    {
        if (_createActive || _controller.IsBusy || _browse is not { CanCreateDirectory: true } browse) return;
        _createActive = true;
        try { await CommandAsync(() => _controller.CreateDirectoryAsync(CollectDirectoryAsync), browse.CurrentPath); }
        finally { _createActive = false; }
    });

    private Task OpenUploadAsync() => Track(async () =>
    {
        if (_uploadOpen || _controller.IsBusy || _browse is not { CanUpload: true } browse || _config is null) return;
        _uploadOpen = true;
        try
        {
            await _dialogs.ShowAsync<FileOpsUploadDialog>(L["Design:DlgUploadTitle"], new()
            {
                [nameof(FileOpsUploadDialog.Directory)] = browse.CurrentPath,
                [nameof(FileOpsUploadDialog.MaximumBytes)] = _config.MaxUploadFileSizeBytes,
                [nameof(FileOpsUploadDialog.Controller)] = _controller,
                [nameof(FileOpsUploadDialog.Upload)] = (Func<IBrowserFile, Task>)UploadAsync
            }, DecisionOptions with { CloseOnEscapeKey = false, BackdropClick = false });
        }
        finally { _uploadOpen = false; }
    });

    private Task UploadAsync(IBrowserFile file) => CommandAsync(
        () => _controller.UploadAsync(file.Name, file.Size,
            (maximum, token) => Task.FromResult<Stream>(file.OpenReadStream(maximum, token)), ConfirmDiscardAsync), file.Name);

    private async Task<string?> CollectDirectoryAsync(FileOpsCreateContext context, CancellationToken token)
    {
        var result = await _dialogs.ShowAsync<FileOpsCreateDirectoryDialog>(L["CreateDirectoryDialog:Title"],
            new() { [nameof(FileOpsCreateDirectoryDialog.ParentPath)] = context.ParentPath }, DecisionOptions);
        if (_retired || result is not { Canceled: false, Data: string name }) return null;
        _pendingTarget = System.IO.Path.Combine(context.ParentPath, name);
        return name;
    }

    private async Task<bool?> ConfirmDeleteAsync(FileOpsDeleteConfirmation context, CancellationToken token)
    {
        _pendingTarget = string.Join(Environment.NewLine, context.Entries.Select(entry => entry.FullPath));
        var result = await _dialogs.ShowAsync<FileOpsBatchDeleteConfirmDialog>(L["BatchDeleteDialog:Title"],
            new()
            {
                [nameof(FileOpsBatchDeleteConfirmDialog.Context)] = context,
                [nameof(FileOpsBatchDeleteConfirmDialog.OriginPath)] = _browse?.CurrentPath ?? ""
            }, DecisionOptions with { MaxWidth = context.Entries.Count > 1 ? MaxWidth.Medium : MaxWidth.Small });
        return !_retired && result is { Canceled: false, Data: bool recursive } ? recursive : null;
    }

    private async Task<bool> ConfirmDiscardAsync(FileOpsDiscardRequest request, CancellationToken token)
    {
        var result = await _dialogs.ShowAsync<FileOpsDiscardDialog>(L["Discard:Title"],
            new() { [nameof(FileOpsDiscardDialog.Request)] = request }, DecisionOptions);
        return !_retired && result is { Canceled: false, Data: true };
    }

    private async Task<bool> ConfirmPolicyDiscardAsync()
    {
        var result = await _dialogs.ShowAsync<FileOpsDiscardDialog>(L["Discard:PolicyTitle"],
            new() { [nameof(FileOpsDiscardDialog.IsPolicy)] = true }, DecisionOptions);
        return !_retired && result is { Canceled: false, Data: true };
    }

    private async Task<bool> CanAccessAsync(string route)
    {
        try { return await PageAccess.IsAuthorizedAsync(route, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
    }

    private async Task ReadPolicyAsync(long? replaceRevision = null)
    {
        await _controller.RefreshPolicyAsync();
        if (_retired) return;
        _policyRead = _controller.LastRead;
        if (_policyRead?.Succeeded == true && _controller.RuntimeConfig is { } config)
        {
            Session.PolicyDraft ??= new(config);
            Session.PolicyDraft.Accept(config, replaceRevision);
        }
    }

    private Task ReloadPolicyAsync() => Track(async () =>
    {
        if (_policyWorking || _controller.IsBusy) return;
        _policyWorking = true;
        try
        {
            _accessDenied = !await CanAccessAsync(UIFileOpsPolicyPage.PAGE_URL);
            if (_retired || _accessDenied) return;
            var revision = PolicyDraft?.Revision;
            if (PolicyDraft?.IsDirty == true && !await ConfirmPolicyDiscardAsync()) return;
            if (revision == PolicyDraft?.Revision) await ReadPolicyAsync(revision);
        }
        finally { _policyWorking = false; }
    });

    private Task ApplyPolicyAsync() => Track(async () =>
    {
        if (_policyWorking || _controller.IsBusy || PolicyDraft is not { IsValid: true, IsDirty: true } form) return;
        _policyWorking = true;
        var revision = form.Revision;
        var candidate = form.Draft.Clone();
        try
        {
            _accessDenied = !await CanAccessAsync(UIFileOpsPolicyPage.PAGE_URL);
            if (_retired || _accessDenied || form.Revision != revision) return;
            var prior = _controller.LastCommand;
            await _controller.SavePolicyAsync(candidate, ConfirmDiscardAsync);
            if (_retired || ReferenceEquals(prior, _controller.LastCommand)) return;
            _policyRead = _controller.LastRead;
            if (_controller.LastCommand?.Succeeded == true && _controller.RuntimeConfig is { } accepted &&
                _policyRead is not { Kind: FileOpsOperationKind.LoadPolicy, Succeeded: false })
            {
                form.Accept(accepted, revision);
                Session.FilesQuery = Session.FilesQuery with { Path = _browse?.CurrentPath, File = SelectedPath };
            }
        }
        finally { _policyWorking = false; }
    });

    private Task OnControllerChangedAsync() => InvokeAsync(() =>
    {
        if (_retired) return;
        if (!ReferenceEquals(_observedCommand, _controller.LastCommand))
        {
            _observedCommand = _controller.LastCommand;
            _commandTarget = _pendingTarget;
        }
        UpdateEvidence();
        StateHasChanged();
    });

    private void UpdateEvidence()
    {
        _browse = _controller.Browse;
        _config = _controller.RuntimeConfig;
        _inventory = new(_controller.SortedEntries, _query);
    }

    private Task Track(Func<Task> action) => Track(async () => { await action(); return true; });

    private async Task<T?> Track<T>(Func<Task<T>> action)
    {
        if (_retired) return default;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _actions.Add(completion.Task);
        try { return await action(); }
        finally
        {
            _actions.Remove(completion.Task);
            completion.SetResult();
            if (!_retired) StateHasChanged();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(_disposal ??= RetireAsync());

    private async Task RetireAsync()
    {
        _retired = true;
        Session.Changed -= OnControllerChangedAsync;
        Navigation.LocationChanged -= OnLocationChanged;
        await _lifetime.CancelAsync();
        try
        {
            await Task.WhenAll(_dialogs.DisposeAsync().AsTask(), _transfer.DisposeAsync().AsTask(),
                Task.WhenAll(_actions.ToArray()));
        }
        finally
        {
            if (!IsFamilyRoute(Navigation.Uri))
                await Session.EndVisitAsync(_controller);
            _lifetime.Dispose();
        }
    }

    private bool IsFilesRoute(string uri) => IsRoute(uri, UIFileOpsPage.PAGE_URL);
    private bool IsFamilyRoute(string uri) => IsFilesRoute(uri) || IsRoute(uri, UIFileOpsPolicyPage.PAGE_URL);
    private bool IsRoute(string uri, string route) => string.Equals(Navigation.ToAbsoluteUri(uri).AbsolutePath.TrimEnd('/'),
        Navigation.ToAbsoluteUri(route).AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    private static bool SamePath(string? left, string? right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
