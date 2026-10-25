using Monica.Core.Results;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Monica.Modules;
using Monica.StateStore.Models;
using Monica.StateStore.UI.Localization;
using Monica.StateStore.UI.Models;
using MudBlazor;

namespace Monica.StateStore.UI.UIStateStore.Components;

public partial class StateStoreKeyExplorer
{
    [Parameter] public List<StateStoreProviderInfo> Providers { get; set; } = [];
    [Parameter] public ModuleStateStoreUIOption Options { get; set; } = new();
    [Parameter] public EventCallback OnRefreshProviders { get; set; }
    /// <summary>Optional caller-owned page identity. Null renders no identity copy; this explorer owns the scoped Create action.</summary>
    [Parameter] public RenderFragment? HeaderContent { get; set; }
    /// <summary>Gets or sets the shared route navigation rendered below the page identity.</summary>
    [Parameter] public RenderFragment? NavigationContent { get; set; }
    /// <summary>Gets or sets the opaque registration identity supplied by the browser route.</summary>
    [Parameter] public string? ProviderIdentity { get; set; }
    /// <summary>Gets or sets the callback that writes a committed provider selection to the route.</summary>
    [Parameter] public EventCallback<string?> ProviderIdentityChanged { get; set; }

    private readonly HashSet<string> selectedKeyNames = new(StringComparer.Ordinal);
    private readonly List<Task> _operations = [];
    private readonly List<CancellationTokenSource> _retiredScopes = [];
    private readonly List<IDialogReference> _dialogs = [];
    private readonly Dictionary<(long Provider, long Result, long Selection, string Key), Task<bool>> _loads = [];
    private readonly string _visit = "state-store-" + Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, object> _structuredInput = new() { ["spellcheck"] = "false" };
    private readonly DialogOptions _confirmationOptions = new() { FullWidth = true, MaxWidth = MaxWidth.Small, CloseOnEscapeKey = true };
    private CancellationTokenSource _providerCancellation = new();
    private CancellationTokenSource _queryCancellation = new();
    private CancellationTokenSource _selectionCancellation = new();
    private StateStoreProviderInfo? selectedProvider, _acceptedProvider, providerDetailTarget;
    private StateStoreKeyInfo? selectedKey;
    private StateStoreKeyBrowseResult? browseResult;
    private List<StateStoreKeyInfo> keys = [];
    private EStateStoreKeySearchMode searchMode = EStateStoreKeySearchMode.Auto;
    private string searchQuery = string.Empty;
    private string? browseError, loadingKeyName;
    private bool hasBrowsed, isSearching, isRefreshingProviders, isProviderDetailVisible, _isMutating, _pickerOpen, isDisposed;
    private long _providerGeneration, _queryGeneration, _resultGeneration, _selectionGeneration, _mutationGeneration, _focusVersion;
    private ElementReference _workspace;
    private Task<IJSObjectReference>? _moduleImport;
    private Task? _retirement;
    private Reveal? _pendingReveal, _pendingBack;
    private Confirmation? _confirmation;
    private bool _treeView, _showDetail, _expanded;
    private string? _previewSource;
    private bool _previewIsJson;

    protected override void OnParametersSet()
    {
        if (isDisposed) return;
        var rebound = Providers.FirstOrDefault(item => item.UniqueId == ProviderIdentity)
            ?? Providers.FirstOrDefault(item => item.UniqueId == selectedProvider?.UniqueId)
            ?? Providers.FirstOrDefault(item => item.IsDefaultStateStore) ?? Providers.FirstOrDefault();
        if (rebound?.UniqueId != selectedProvider?.UniqueId) ChangeProvider(rebound);
        else selectedProvider = rebound;
    }

    private async Task SelectProviderIdentity(string identity)
    {
        if (isDisposed) return;
        ChangeProvider(Providers.FirstOrDefault(item => item.UniqueId == identity));
        if (!isDisposed) await ProviderIdentityChanged.InvokeAsync(identity);
    }
    private void ChangeProvider(StateStoreProviderInfo? provider)
    {
        if (isDisposed || selectedProvider?.UniqueId == provider?.UniqueId) return;
        CancelFocusIntent();
        var cleanupErrors = new List<Exception>();
        // Retire the identity before cancellation callbacks can re-enter this owner.
        _providerGeneration++; _queryGeneration++; _resultGeneration++; _selectionGeneration++; _mutationGeneration++;
        try { _providerCancellation.Cancel(); }
        catch (Exception error) { cleanupErrors.Add(error); }
        _retiredScopes.Add(_providerCancellation);
        _providerCancellation = new();
        RenewCancellation(ref _queryCancellation, _providerCancellation.Token, cleanupErrors);
        RenewCancellation(ref _selectionCancellation, _queryCancellation.Token, cleanupErrors);
        foreach (var dialog in _dialogs.ToArray()) CloseOwnedDialog(dialog, cleanupErrors);
        CompleteConfirmation(false);
        selectedProvider = provider;
        _acceptedProvider = null; browseResult = null; selectedKey = null; keys = []; selectedKeyNames.Clear();
        browseError = null; loadingKeyName = null; hasBrowsed = false; isSearching = false; _isMutating = false;
        _pendingReveal = null; _pickerOpen = false; isProviderDetailVisible = false; providerDetailTarget = null;
        _showDetail = false; _expanded = false;
        searchMode = provider is null ? EStateStoreKeySearchMode.Auto : NormalizeSearchMode(provider, provider.DefaultSearchMode);
        searchQuery = searchMode == EStateStoreKeySearchMode.PatternScan ? Options.DefaultScanPattern : string.Empty;
        if (cleanupErrors.Count > 0)
            browseError = L["KeyExplorer:Messages:ProviderSwitchCleanupFailed", new AggregateException(cleanupErrors).Message];
    }
    private void RenewCancellation(ref CancellationTokenSource cancellation, CancellationToken parent, List<Exception>? errors = null)
    {
        var previous = cancellation;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
        _retiredScopes.Add(previous);
        try { previous.Cancel(); }
        catch (Exception error) when (errors is not null) { errors.Add(error); }
    }
    private static void CloseOwnedDialog(IDialogReference dialog, List<Exception> errors)
    {
        try { dialog.Close(DialogResult.Cancel()); }
        catch (Exception error)
        {
            errors.Add(error);
            try { dialog.Dismiss(DialogResult.Cancel()); }
            catch (Exception dismissalError) { errors.Add(dismissalError); }
        }
    }
    private Scope CaptureScope() => new(_providerGeneration, selectedProvider!.UniqueId, selectedProvider.ServiceKey, _providerCancellation.Token);
    private bool Current(Scope scope) => !isDisposed && !scope.Token.IsCancellationRequested
        && scope.Generation == _providerGeneration && scope.Identity == selectedProvider?.UniqueId && scope.ServiceKey == selectedProvider?.ServiceKey;
    private Task Track(Func<Task> action)
    {
        if (isDisposed) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.Add(completion.Task);
        _ = CompleteOperationAsync();
        return completion.Task;
        async Task CompleteOperationAsync()
        {
            await CompleteTrackedAsync(action, completion);
            _operations.Remove(completion.Task);
        }
    }
    private static async Task CompleteTrackedAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try { await action(); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }
    private void CancelFocusIntent() { _focusVersion++; _pendingReveal = null; _pendingBack = null; }
    private void ChangeQuery(string value) { CancelFocusIntent(); searchQuery = value; }
    private void SetSearchMode(EStateStoreKeySearchMode mode)
    {
        if (selectedProvider is null || isDisposed) return;
        CancelFocusIntent();
        var defaultBefore = searchMode == EStateStoreKeySearchMode.PatternScan ? Options.DefaultScanPattern : string.Empty;
        searchMode = NormalizeSearchMode(selectedProvider, mode);
        if (string.IsNullOrWhiteSpace(searchQuery) || searchQuery == defaultBefore)
            searchQuery = searchMode == EStateStoreKeySearchMode.PatternScan ? Options.DefaultScanPattern : string.Empty;
    }
    private Task OnSearchKeyDown(KeyboardEventArgs args) => args.Key == "Enter" ? SearchKeysAsync() : Task.CompletedTask;
    private Task SearchKeysAsync() => Track(SearchCoreAsync);
    private async Task SearchCoreAsync()
    {
        if (!CanSearch()) return;
        CancelFocusIntent();
        var scope = CaptureScope(); var generation = ++_queryGeneration;
        RenewCancellation(ref _queryCancellation, scope.Token);
        RenewCancellation(ref _selectionCancellation, _queryCancellation.Token);
        var queryToken = _queryCancellation.Token;
        var request = new StateStoreKeyBrowseRequest { Query = searchQuery.Trim(), SearchMode = searchMode, Limit = Options.MaxKeysPerPage };
        isSearching = true; hasBrowsed = true; browseError = null; browseResult = null; _acceptedProvider = null;
        keys = []; selectedKey = null; selectedKeyNames.Clear(); loadingKeyName = null;
        _showDetail = false;
        _resultGeneration++; _selectionGeneration++; _pendingReveal = null; _pickerOpen = false;
        RequestRender();
        try
        {
            var result = await StateStoreService.SearchKeysAsync(scope.ServiceKey, request, queryToken);
            if (!Current(scope) || generation != _queryGeneration) return;
            if (result.IsFailed(out var error)) { browseError = error.Message; return; }
            browseResult = result.Data! with { Items = result.Data!.Items.Select(Copy).ToList() };
            _acceptedProvider = selectedProvider; keys = browseResult.Items.ToList();
            if (keys.Count > 0) await SelectCoreAsync(keys[0], false);
        }
        catch (OperationCanceledException) when (!Current(scope) || queryToken.IsCancellationRequested) { }
        catch (Exception error) { if (Current(scope) && generation == _queryGeneration) browseError = error.Message; }
        finally { if (Current(scope) && generation == _queryGeneration) { isSearching = false; RequestRender(); } }
    }
    private Task SelectKeyManualAsync(StateStoreKeyInfo key) => Track(() => SelectCoreAsync(key, true));
    private async Task SelectCoreAsync(StateStoreKeyInfo key, bool manual)
    {
        if (selectedProvider is null || !keys.Contains(key)) return;
        CancelFocusIntent();
        var focus = _focusVersion;
        var scope = CaptureScope(); var result = _resultGeneration; var selection = ++_selectionGeneration;
        RenewCancellation(ref _selectionCancellation, _queryCancellation.Token);
        selectedKey = key; _pickerOpen = false; _pendingReveal = null; RequestRender();
        if (manual) _showDetail = true;
        await LoadCoreAsync(key, scope, result, selection);
        if (manual && Current(scope) && focus == _focusVersion && result == _resultGeneration && selection == _selectionGeneration && selectedKey?.Key == key.Key)
        { _pendingReveal = new(scope.Generation, result, selection, focus, key.Key); RequestRender(); }
    }
    private Task<bool> LoadCoreAsync(StateStoreKeyInfo key, Scope scope, long result, long selection, bool explicitLoad = false)
    {
        var identity = (scope.Generation, result, selection, key.Key);
        if (_loads.TryGetValue(identity, out var admitted)) return admitted;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loads.Add(identity, completion.Task);
        _ = CompleteLoadAsync();
        return completion.Task;
        async Task CompleteLoadAsync()
        {
            try { completion.TrySetResult(await LoadOnceAsync(key, scope, result, selection, explicitLoad)); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { _loads.Remove(identity); }
        }
    }
    private async Task<bool> LoadOnceAsync(StateStoreKeyInfo key, Scope scope, long result, long selection, bool explicitLoad)
    {
        if (!Current(scope) || result != _resultGeneration || !keys.Contains(key)) return false;
        // Automatic inline preview has its own feature gate; explicit View/Edit still load exact-key evidence.
        if (key.IsValueLoaded || !explicitLoad && selectedProvider?.SupportsValuePreview != true) return true;
        var loadToken = _selectionCancellation.Token;
        loadingKeyName = key.Key; RequestRender();
        try
        {
            var response = await StateStoreService.GetKeyAsync(scope.ServiceKey, key.Key, loadToken);
            if (!Current(scope) || result != _resultGeneration || selection != _selectionGeneration || !keys.Contains(key)) return false;
            if (response.IsOk(out var loaded)) CopyInto(key, loaded);
            else { key.Error = response.Message; key.IsValueLoaded = true; }
            return true;
        }
        catch (OperationCanceledException) when (!Current(scope) || loadToken.IsCancellationRequested) { return false; }
        finally
        {
            if (Current(scope) && result == _resultGeneration && selection == _selectionGeneration)
            { loadingKeyName = null; RequestRender(); }
        }
    }
    private Task RefreshProvidersAsync() => Track(async () =>
    {
        if (!OnRefreshProviders.HasDelegate || isRefreshingProviders) return;
        isRefreshingProviders = true;
        try { await OnRefreshProviders.InvokeAsync(); }
        finally { if (!isDisposed) { isRefreshingProviders = false; RequestRender(); } }
    });
    private void OpenProviderDetail(StateStoreProviderInfo provider) { if (!isDisposed) { providerDetailTarget = provider; isProviderDetailVisible = true; } }
    private Task OnProviderDetailVisibilityChanged(bool visible) { if (!isDisposed) { isProviderDetailVisible = visible; if (!visible) providerDetailTarget = null; } return Task.CompletedTask; }

    private Task ViewKeyAsync(StateStoreKeyInfo key) => Track(() => OpenEditorAsync(key, true));
    private Task EditKeyAsync(StateStoreKeyInfo key) => Track(() => OpenEditorAsync(key, false));
    private async Task OpenEditorAsync(StateStoreKeyInfo key, bool readOnly)
    {
        if (selectedProvider is null || (readOnly ? !CanViewKeys() : !CanEditSelectedProviderKeys())) return;
        CancelFocusIntent();
        var scope = CaptureScope(); var result = _resultGeneration;
        if (!await LoadCoreAsync(key, scope, result, _selectionGeneration, explicitLoad: true) || !Current(scope) || result != _resultGeneration) return;
        if (!readOnly && (!key.IsValueLoaded || !string.IsNullOrWhiteSpace(key.Error))) return;
        var parameters = new DialogParameters<KeyEditorDialog>
        {
            { item => item.KeyInfo, Copy(key) }, { item => item.ServiceKey, scope.ServiceKey }, { item => item.ReadOnly, readOnly },
            { item => item.ScopeCancellationToken, scope.Token },
            { item => item.OnSaved, EventCallback.Factory.Create<StateStoreKeyInfo>(this, updated => PublishSaved(scope, result, updated)) }
        };
        await ShowOwnedAsync<KeyEditorDialog>(scope, parameters, L[readOnly ? "KeyExplorer:Dialogs:ViewKeyTitle" : "KeyExplorer:Dialogs:EditKeyTitle"]);
    }
    private Task OpenCreateKeyDialog() => Track(async () =>
    {
        if (!CanCreateSelectedProviderKeys()) return;
        CancelFocusIntent();
        var scope = CaptureScope(); var result = _resultGeneration;
        var parameters = new DialogParameters<KeyCreateDialog>
        {
            { item => item.ServiceKey, scope.ServiceKey }, { item => item.ScopeCancellationToken, scope.Token },
            { item => item.OnCreated, EventCallback.Factory.Create<StateStoreKeyInfo>(this, created => PublishCreated(scope, result, created)) }
        };
        await ShowOwnedAsync<KeyCreateDialog>(scope, parameters, L["KeyCreate:Dialogs:Title"]);
        if (Current(scope) && result != _resultGeneration && selectedKey is { } createdKey && browseResult?.Query == createdKey.Key)
        {
            // The compact layout hides the Create trigger. Reveal only after its dialog has retired.
            _pendingReveal = new(_providerGeneration, _resultGeneration, _selectionGeneration, _focusVersion, createdKey.Key);
            RequestRender();
        }
    });
    private async Task ShowOwnedAsync<T>(Scope scope, DialogParameters<T> parameters, string title) where T : IComponent
    {
        var dialog = await DialogService.ShowAsync<T>(title, parameters, new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Large });
        var ownedComponent = dialog.Dialog as IAsyncDisposable;
        _dialogs.Add(dialog);
        try
        {
            if (!Current(scope)) dialog.Close(DialogResult.Cancel());
            await dialog.Result;
        }
        finally
        {
            try { if (ownedComponent is not null) await ownedComponent.DisposeAsync(); }
            finally { _dialogs.Remove(dialog); }
        }
    }
    private void PublishSaved(Scope scope, long result, StateStoreKeyInfo updated)
    {
        if (!Current(scope) || result != _resultGeneration) return;
        CancelFocusIntent();
        var existing = keys.FirstOrDefault(item => item.Key == updated.Key);
        if (existing is not null) CopyInto(existing, updated);
        if (selectedKey?.Key == updated.Key) selectedKey = existing ?? Copy(updated);
        Snackbar.Add(L["KeyExplorer:Messages:KeySaveSuccess"], Severity.Success); RequestRender();
    }
    private void PublishCreated(Scope scope, long result, StateStoreKeyInfo created)
    {
        if (!Current(scope) || result != _resultGeneration) return;
        CancelFocusIntent();
        _queryGeneration++; _resultGeneration++; _selectionGeneration++; _pendingReveal = null; isSearching = false; loadingKeyName = null;
        var copy = Copy(created); searchMode = NormalizeSearchMode(selectedProvider!, EStateStoreKeySearchMode.ExactKey); searchQuery = copy.Key;
        keys = [copy]; selectedKey = copy; selectedKeyNames.Clear(); browseError = null; hasBrowsed = true; _pickerOpen = false;
        _acceptedProvider = selectedProvider; browseResult = new() { Query = copy.Key, AppliedMode = searchMode, Items = keys.ToList(), TotalCount = 1 };
        _showDetail = true;
        Snackbar.Add(L["KeyExplorer:Messages:KeyCreateSuccess"], Severity.Success); RequestRender();
    }
    private Task ReloadSelectedKeyAsync() => Track(async () =>
    {
        if (selectedKey is null || selectedProvider is null) return;
        CancelFocusIntent();
        var key = selectedKey; key.IsValueLoaded = false;
        var scope = CaptureScope(); var result = _resultGeneration; var selection = ++_selectionGeneration;
        RenewCancellation(ref _selectionCancellation, _queryCancellation.Token);
        await LoadCoreAsync(key, scope, result, selection);
    });
    private Task DeleteKeyAsync(StateStoreKeyInfo key) => Track(() => DeleteCoreAsync([key.Key], key.ETag, false));
    private Task DeleteSelectedKeysAsync() => Track(() => DeleteCoreAsync(selectedKeyNames.Order(StringComparer.Ordinal).ToArray(), null, true));
    private async Task DeleteCoreAsync(string[] names, string? etag, bool batch)
    {
        if (_isMutating || selectedProvider is null || names.Length == 0 || !(batch ? CanShowRowSelection() : CanDeleteSelectedProviderKey())) return;
        CancelFocusIntent();
        var scope = CaptureScope(); var result = _resultGeneration; var mutation = ++_mutationGeneration;
        _isMutating = true;
        try
        {
            var confirmed = await ConfirmAsync(batch, names, GetProviderCaption(selectedProvider));
            if (!confirmed || !Current(scope) || result != _resultGeneration || !(batch ? CanShowRowSelection() : CanDeleteSelectedProviderKey())) return;
            var response = batch ? await StateStoreService.DeleteKeysAsync(scope.ServiceKey, names, scope.Token)
                : await StateStoreService.DeleteKeyAsync(scope.ServiceKey, names[0], etag, scope.Token);
            if (!Current(scope) || result != _resultGeneration) return;
            if (response.IsFailed(out var error)) { Snackbar.Add(L["KeyExplorer:Messages:KeyDeleteFailed", error.Message ?? string.Empty], Severity.Error); return; }
            var captured = names.ToHashSet(StringComparer.Ordinal); var removed = keys.RemoveAll(item => captured.Contains(item.Key));
            selectedKeyNames.ExceptWith(captured);
            browseResult = browseResult! with { Items = keys.ToList(), TotalCount = Math.Max(0, browseResult.TotalCount - removed) };
            if (selectedKey is not null && captured.Contains(selectedKey.Key))
            { selectedKey = null; if (keys.Count > 0) await SelectCoreAsync(keys[0], false); }
            if (Current(scope) && result == _resultGeneration) { Snackbar.Add(response.Message ?? L["KeyExplorer:Messages:KeyDeleteSuccess"], Severity.Success); RequestRender(); }
        }
        finally { if (Current(scope) && mutation == _mutationGeneration) { _isMutating = false; RequestRender(); } }
    }
    private Task<bool> ConfirmAsync(bool batch, string[] names, string provider)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmation = new(L[batch ? "KeyExplorer:Dialogs:BatchDeleteTitle" : "KeyExplorer:Dialogs:DeleteKeyTitle"],
            batch ? L["KeyExplorer:Dialogs:BatchDeleteMessage", names.Length] : L["KeyExplorer:Dialogs:DeleteKeyMessage", names[0]],
            L[batch ? "KeyExplorer:Dialogs:BatchDeleteConfirm" : "KeyExplorer:Dialogs:DeleteKeyConfirm"], provider, completion);
        RequestRender(); return completion.Task;
    }
    private void CompleteConfirmation(bool confirmed) { var current = _confirmation; _confirmation = null; current?.Completion.TrySetResult(confirmed); RequestRender(); }
    private Task OnConfirmationVisibilityChanged(bool visible) { if (!visible) CompleteConfirmation(false); return Task.CompletedTask; }
    private void ToggleKeySelection(StateStoreKeyInfo key, bool selected) { if (selected) selectedKeyNames.Add(key.Key); else selectedKeyNames.Remove(key.Key); }
    private void ToggleAllSelection(bool selected)
    {
        selectedKeyNames.Clear();
        if (selected) foreach (var key in keys) selectedKeyNames.Add(key.Key);
    }
    private void TogglePicker()
    {
        if (isDisposed) return;
        CancelFocusIntent();
        _pickerOpen = !_pickerOpen;
        _pendingBack = _pickerOpen && selectedKey is not null
            ? new(_providerGeneration, _resultGeneration, _selectionGeneration, _focusVersion, selectedKey.Key)
            : null;
    }

    protected override Task OnAfterRenderAsync(bool firstRender) => Track(async () =>
    {
        _moduleImport ??= JS.InvokeAsync<IJSObjectReference>("import", "./_content/Monica.StateStore.UI/UIStateStore/state-workspace.js").AsTask();
        try
        {
            var module = await _moduleImport;
            if (isDisposed) return;
            if (firstRender) await module.InvokeVoidAsync("observe", _workspace, _visit);
            await module.InvokeVoidAsync("sync", _workspace, _visit);
            if (_pendingBack is { } back)
            {
                _pendingBack = null;
                if (!isDisposed && back.Provider == _providerGeneration && back.Result == _resultGeneration && back.Selection == _selectionGeneration && back.Focus == _focusVersion && _pickerOpen)
                    await module.InvokeVoidAsync("back", _workspace, _visit, back.Provider, back.Result, back.Selection, back.Focus, back.Key);
            }
            if (_pendingReveal is { } reveal)
            {
                _pendingReveal = null;
                if (!isDisposed && reveal.Provider == _providerGeneration && reveal.Result == _resultGeneration && reveal.Selection == _selectionGeneration && reveal.Focus == _focusVersion && !_pickerOpen)
                    await module.InvokeVoidAsync("reveal", _workspace, _visit, reveal.Provider, reveal.Result, reveal.Selection, reveal.Focus, reveal.Key);
            }
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) when (isDisposed) { }
    });
    private Task BackToKeysAsync()
    {
        if (!isDisposed && selectedKey is not null)
        {
            CancelFocusIntent();
            _pickerOpen = true;
            _showDetail = false; _expanded = false;
            _pendingBack = new(_providerGeneration, _resultGeneration, _selectionGeneration, _focusVersion, selectedKey.Key);
            RequestRender();
        }
        return Task.CompletedTask;
    }
    private void RequestRender() { if (!isDisposed) StateHasChanged(); }
    private Task CopyKeyAsync() => CopyAsync(selectedKey?.Key);
    private Task CopyValueAsync() => CopyAsync(selectedKey?.RawValue);
    private Task CopyAsync(string? text) => Track(async () =>
    {
        if (text is null || _moduleImport is null) return;
        try
        {
            var copied = await (await _moduleImport).InvokeAsync<bool>("copy", text);
            if (!isDisposed) Snackbar.Add(L[copied ? "Workbench:Copied" : "Workbench:CopyFailed"], copied ? Severity.Success : Severity.Warning);
        }
        catch (JSDisconnectedException) { }
    });
    private string GetValueFormat()
    {
        if (_previewSource != selectedKey?.RawValue)
        {
            _previewSource = selectedKey?.RawValue; _previewIsJson = false;
            if (_previewSource is not null)
            {
                try { using var value = System.Text.Json.JsonDocument.Parse(_previewSource); _previewIsJson = true; }
                catch (System.Text.Json.JsonException) { }
            }
        }
        return L[_previewIsJson ? "Workbench:Json" : "Workbench:Text"];
    }
    public void Dispose() => _ = DisposeAsync();
    public ValueTask DisposeAsync()
    {
        if (_retirement is not null) return new(_retirement);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _retirement = completion.Task;
        _ = CompleteTrackedAsync(RetireAsync, completion);
        return new(_retirement);
    }
    private async Task RetireAsync()
    {
        isDisposed = true; CancelFocusIntent();
        var errors = new List<Exception>();
        foreach (var cancellation in new[] { _providerCancellation, _queryCancellation, _selectionCancellation })
        {
            try { cancellation.Cancel(); }
            catch (Exception error) { errors.Add(error); }
        }
        _confirmation?.Completion.TrySetResult(false); _confirmation = null;
        foreach (var dialog in _dialogs.ToArray()) CloseOwnedDialog(dialog, errors);
        if (_moduleImport is not null)
        {
            try { await (await _moduleImport).InvokeVoidAsync("stop", _workspace, _visit); }
            catch (JSDisconnectedException) { } catch (TaskCanceledException) { }
            catch (Exception error) { errors.Add(error); }
        }
        try { await Task.WhenAll(_operations.ToArray()); }
        catch (Exception error) { errors.Add(error); }
        if (_moduleImport is not null)
        {
            try { await (await _moduleImport).DisposeAsync(); }
            catch (JSDisconnectedException) { } catch (TaskCanceledException) { }
            catch (Exception error) { errors.Add(error); }
        }
        _providerCancellation.Dispose(); _queryCancellation.Dispose(); _selectionCancellation.Dispose();
        foreach (var cancellation in _retiredScopes) cancellation.Dispose();
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private bool CanSearch() => !isDisposed && selectedProvider is not null && !isSearching && !string.IsNullOrWhiteSpace(searchQuery);
    private bool CanViewKeys() => selectedProvider?.SupportsExactLookup == true;
    private bool CanCreateSelectedProviderKeys() => !isDisposed && Options.AllowKeyCreation && selectedProvider?.SupportsKeyCreation == true;
    private bool CanEditSelectedProviderKeys() => !isDisposed && Options.AllowKeyEditing && selectedProvider?.SupportsKeyUpdate == true;
    private bool CanDeleteSelectedProviderKey() => !isDisposed && Options.AllowKeyDeletion && selectedProvider?.SupportsKeyDeletion == true;
    private bool CanShowRowSelection() => Options.AllowKeyDeletion && selectedProvider?.SupportsBulkDelete == true;
    private bool IsSearchMode(EStateStoreKeySearchMode mode) => searchMode == mode;
    private string GetProviderCaption(StateStoreProviderInfo provider) => L.GetProviderDisplayName(provider) + " · " + L.GetProviderTypeText(provider.ProviderType);
    private string GetSearchLabel() => L[searchMode == EStateStoreKeySearchMode.PatternScan ? "KeyExplorer:Labels:KeyPattern" : "KeyExplorer:Labels:KeyName"];
    private string GetSearchPlaceholder() => L[searchMode == EStateStoreKeySearchMode.PatternScan ? "KeyExplorer:Placeholders:PatternSearch" : "KeyExplorer:Placeholders:FullKeyName"];
    private string GetSearchActionText() => L[searchMode == EStateStoreKeySearchMode.PatternScan ? "KeyExplorer:Actions:Scan" : "KeyExplorer:Actions:Query"];
    private string GetSearchModeText(EStateStoreKeySearchMode mode) => L[mode == EStateStoreKeySearchMode.PatternScan ? "KeyExplorer:Labels:PatternSearchMode" : "KeyExplorer:Labels:ExactLookupMode"];
    private string GetSearchModeDescription() => searchMode == EStateStoreKeySearchMode.PatternScan ? L["KeyExplorer:Messages:PatternSearchDescription", Options.MaxKeysPerPage] : L["KeyExplorer:Messages:ExactSearchDescription"];
    private string GetWorkspaceSupportMessage() => selectedProvider is not null && !selectedProvider.SupportsKeyScanning ? L["KeyExplorer:Messages:ExactOnlyProvider"] : string.Empty;
    private string GetResultRowClass(StateStoreKeyInfo key) => selectedKey?.Key == key.Key ? "result-row is-selected" : "result-row";
    private string GetResultPreviewText(StateStoreKeyInfo key) => key.RawValue is not null ? ToPreviewText(key.RawValue) : key.Error ?? (key.IsValueLoaded ? L["KeyValuePreview:Labels:Empty"] : L["KeyExplorer:Statuses:SelectToInspect"]);
    private string GetResultStatusText(StateStoreKeyInfo key) => L[loadingKeyName == key.Key ? "KeyExplorer:Statuses:Loading" : !string.IsNullOrWhiteSpace(key.Error) ? "KeyExplorer:Statuses:LoadFailed" : key.IsValueLoaded ? key.RawValue is null ? "KeyValuePreview:Labels:Empty" : "KeyExplorer:Statuses:ValueLoaded" : "KeyExplorer:Statuses:SelectToInspect"];
    private Color GetResultStatusColor(StateStoreKeyInfo key) => loadingKeyName == key.Key ? Color.Info : !string.IsNullOrWhiteSpace(key.Error) ? Color.Error : key.IsValueLoaded && key.RawValue is not null ? Color.Success : Color.Default;
    private string GetTtlText(StateStoreKeyInfo key) => key.TTLStatus switch
    { EStateStoreKeyTtlStatus.Expiring when key.TTL.HasValue => (key.TTL.Value < TimeSpan.Zero ? TimeSpan.Zero : key.TTL.Value).ToString(@"d\.hh\:mm\:ss"), EStateStoreKeyTtlStatus.Permanent => L["KeyExplorer:Detail:TTLPermanent"], EStateStoreKeyTtlStatus.Unsupported => L["KeyExplorer:Detail:TTLUnavailable"], _ => L["KeyExplorer:Detail:TTLUnknown"] };
    private static EStateStoreKeySearchMode NormalizeSearchMode(StateStoreProviderInfo provider, EStateStoreKeySearchMode mode) => mode == EStateStoreKeySearchMode.PatternScan && provider.SupportsKeyScanning ? mode : provider.SupportsExactLookup ? EStateStoreKeySearchMode.ExactKey : EStateStoreKeySearchMode.PatternScan;
    private static string ToPreviewText(string value) { var text = value.Replace("\r", string.Empty).Replace("\n", " ").Trim(); return text.Length > 120 ? text[..120] + "…" : text; }
    private static StateStoreKeyInfo Copy(StateStoreKeyInfo value) => new() { Key = value.Key, RawValue = value.RawValue, ETag = value.ETag, TTL = value.TTL, TTLStatus = value.TTLStatus, IsValueLoaded = value.IsValueLoaded, Error = value.Error };
    private static void CopyInto(StateStoreKeyInfo target, StateStoreKeyInfo source) { target.RawValue = source.RawValue; target.ETag = source.ETag; target.TTL = source.TTL; target.TTLStatus = source.TTLStatus; target.IsValueLoaded = source.IsValueLoaded; target.Error = source.Error; }
    private sealed record Scope(long Generation, string Identity, string? ServiceKey, CancellationToken Token);
    private sealed record Reveal(long Provider, long Result, long Selection, long Focus, string Key);
    private sealed record Confirmation(string Title, string Message, string ConfirmLabel, string Provider, TaskCompletionSource<bool> Completion);
}
