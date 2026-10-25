using Monica.Core.Results;
using Monica.DevOps.K8S.Facades;
using Monica.DevOps.K8S.Models;
using Monica.UI.Shell.Support;

namespace Monica.DevOps.K8S.State;

/// <summary>Coordinates evidence and complete operation chains for one Inventory visit.</summary>
internal sealed class K8SInventoryVisitController(K8SFacade facade, K8SNavigationState navigation,
    Func<Task> publish) : K8SVisitController(publish)
{
    private const string VIEW_STORAGE_KEY = "k8s:view-context";
    private readonly bool _restoreBrowser = !navigation.HasContext;
    private readonly HashSet<K8SResourceIdentity> _selected = [];
    private long _contextVersion;
    private Task _storageWrite = Task.CompletedTask;

    internal string Namespace { get; private set; } = navigation.Namespace;
    internal K8SResourceType ResourceType { get; private set; } = navigation.ResourceType;
    internal string Search { get; private set; } = navigation.Search;
    internal string? ExecutionNode { get; private set; }
    internal IReadOnlyList<K8SNamespaceInfo>? Namespaces { get; private set; }
    internal K8SNamespaceInventoryResult? NamespaceCapture { get; private set; }
    internal K8SResourceListResult? Capture { get; private set; }
    internal K8SReadFailure? ReadFailure { get; private set; }
    internal string? ContextFailure { get; private set; }
    internal K8SOperationOutcome? LatestOperation { get; private set; }
    internal K8SInventoryActivity Activity { get; private set; }
    internal string ActivityIdentity { get; private set; } = string.Empty;
    internal bool IsInitialized { get; private set; }
    internal bool ContextMatchesCapture => Capture is not null && Capture.Namespace == Namespace && Capture.ResourceType == ResourceType;
    internal bool ContextMatchesNamespaceCapture => NamespaceCapture is not null && NamespaceCapture.Namespace == Namespace;
    internal bool NamespaceExists => Namespaces?.Any(x => x.Name == Namespace && x.Exists && x.IsInScope) == true;
    internal bool CanOperate => !IsBusy && !IsRetired && NamespaceExists && ContextMatchesCapture;
    internal int SelectedCount => _selected.Count;
    internal HashSet<K8SResourceSummary> SelectedRows => Capture?.Items
        .Where(x => _selected.Contains(K8SResourceIdentity.From(x)))
        .ToHashSet(K8SResourceIdentity.Comparer) ?? new(K8SResourceIdentity.Comparer);

    internal Task InitializeAsync() => RunAsync(async token =>
    {
        await RefreshContextCoreAsync(token);
        token.ThrowIfCancellationRequested();
        IsInitialized = true;
    });

    internal Task RefreshAllAsync() => RunAsync(RefreshContextCoreAsync);
    internal Task RefreshListAsync() => RunAsync(ReadListCoreAsync);

    internal Task RestoreBrowserAsync(IBrowserStorage storage) => RunAsync(async token =>
    {
        if (!_restoreBrowser || _contextVersion != 0) return;
        await SetActivityAsync(K8SInventoryActivity.Reading, Namespace, ResourceType, token);
        var context = await storage.GetAsync<K8SViewContext?>(VIEW_STORAGE_KEY, null, BrowserStorageType.Session);
        token.ThrowIfCancellationRequested();
        if (context is null || !Enum.IsDefined(context.ResourceType)) return;
        Namespace = context.Namespace ?? string.Empty;
        ResourceType = context.ResourceType;
        Search = context.Search ?? string.Empty;
        _selected.Clear();
        RememberContext();
        await AdoptContextCoreAsync(token);
    });

    internal Task PersistBrowserAsync(IBrowserStorage storage)
    {
        var context = new K8SViewContext(Namespace, ResourceType, Search);
        var previous = _storageWrite;
        return _storageWrite = RunPresentationAsync(async token =>
        {
            await previous;
            token.ThrowIfCancellationRequested();
            await storage.TrySetAsync(VIEW_STORAGE_KEY, context, BrowserStorageType.Session);
            token.ThrowIfCancellationRequested();
        });
    }

    internal Task ChangeContextAsync(string namespaceName, K8SResourceType type) => RunAsync(async token =>
    {
        if (!Enum.IsDefined(type)) return;
        Namespace = namespaceName;
        ResourceType = type;
        _contextVersion++;
        _selected.Clear();
        RememberContext();
        await AdoptContextCoreAsync(token);
    });

    internal void SetSearch(string value)
    {
        if (IsRetired) return;
        Search = value;
        _contextVersion++;
        RememberContext();
    }

    internal void SetSelected(IEnumerable<K8SResourceSummary> rows)
    {
        if (IsBusy || IsRetired || !ContextMatchesCapture) return;
        var capturedIdentities = Capture!.Items.Select(K8SResourceIdentity.From).ToHashSet();
        _selected.Clear();
        foreach (var row in rows)
        {
            var identity = K8SResourceIdentity.From(row);
            if (capturedIdentities.Contains(identity)) _selected.Add(identity);
        }
    }

    internal void ClearSelected() { if (!IsBusy && !IsRetired) _selected.Clear(); }

    internal int HiddenSelectionCount(IEnumerable<K8SResourceSummary> visibleRows) =>
        _selected.Except(visibleRows.Select(K8SResourceIdentity.From)).Count();

    internal K8SOperationRequest? CreateRequest(K8SInventoryOperation operation, K8SOperationScope scope,
        K8SResourceSummary? row = null)
    {
        if (!CanOperate) return null;
        var names = scope == K8SOperationScope.Single && row is not null ? [row.Name] : _selected.Select(x => x.Name).ToArray();
        if (scope != K8SOperationScope.Namespace && names.Length == 0) return null;
        return K8SOperationRequest.Create(row?.Namespace ?? Namespace,
            scope == K8SOperationScope.Namespace ? K8SResourceType.Service : row?.ResourceType ?? ResourceType,
            scope, operation, scope == K8SOperationScope.Namespace ? [] : names);
    }

    internal Task DescribeAsync(K8SResourceSummary row, Func<K8SResourceDetails, CancellationToken, Task> show) => RunAsync(async token =>
    {
        var identity = K8SResourceIdentity.From(row);
        await SetActivityAsync(K8SInventoryActivity.Inspecting, identity.Namespace, identity.ResourceType, token, [identity.Name]);
        var response = await facade.DescribeResourceAsync(identity.Namespace, identity.ResourceType, identity.Name, token);
        token.ThrowIfCancellationRequested();
        if (response.IsFailed(out var error, out var details))
            ReadFailure = new(identity.Namespace, identity.ResourceType, error.Message ?? string.Empty);
        else await show(details, token);
        token.ThrowIfCancellationRequested();
    });

    internal Task OperateAsync(K8SOperationRequest request,
        Func<K8SRestartPreview, K8SOperationRequest, CancellationToken, Task<bool>> confirmRestart,
        Func<K8SScalePreview, K8SOperationRequest, CancellationToken, Task<bool>> confirmScale) => RunAsync(async token =>
    {
        await SetActivityAsync(K8SInventoryActivity.Previewing, request.Namespace, request.ResourceType, token, request.Names);
        if (request.Operation == K8SInventoryOperation.Restart)
        {
            var previewResponse = request.Scope == K8SOperationScope.Namespace
                ? await facade.GetNamespaceRestartPreviewAsync(request.Namespace, token)
                : await facade.GetBatchRestartPreviewAsync(request.Namespace, request.ResourceType, request.Names, token);
            token.ThrowIfCancellationRequested();
            if (previewResponse.IsFailed(out var error, out var preview))
            {
                ReadFailure = new(request.Namespace, request.ResourceType, error.Message ?? string.Empty);
                return;
            }
            await SetActivityAsync(K8SInventoryActivity.Reviewing, request.Namespace, request.ResourceType, token, request.Names);
            var confirmed = await confirmRestart(preview, request, token);
            token.ThrowIfCancellationRequested();
            if (!confirmed || !preview.HasRestartableTargets) return;
            await SetActivityAsync(K8SInventoryActivity.Executing, request.Namespace, request.ResourceType, token, request.Names);
            var response = request.Scope == K8SOperationScope.Namespace
                ? await facade.RestartNamespaceAsync(request.Namespace, token)
                : await facade.RestartResourcesAsync(request.Namespace, request.ResourceType, request.Names, token);
            token.ThrowIfCancellationRequested();
            LatestOperation = response.IsFailed(out var commandError, out var result)
                ? new(request, DateTimeOffset.UtcNow, null, null, commandError.Message ?? string.Empty)
                : new(request, result.CompletedAtUtc, result, null, null);
        }
        else
        {
            var operation = request.Operation == K8SInventoryOperation.ScaleDown ? K8SScaleOperation.ScaleDown : K8SScaleOperation.ScaleUp;
            var previewResponse = request.Scope == K8SOperationScope.Namespace
                ? await facade.GetNamespaceScalePreviewAsync(request.Namespace, operation, token)
                : await facade.GetBatchScalePreviewAsync(request.Namespace, request.ResourceType, request.Names, operation, token);
            token.ThrowIfCancellationRequested();
            if (previewResponse.IsFailed(out var error, out var preview))
            {
                ReadFailure = new(request.Namespace, request.ResourceType, error.Message ?? string.Empty);
                return;
            }
            await SetActivityAsync(K8SInventoryActivity.Reviewing, request.Namespace, request.ResourceType, token, request.Names);
            var confirmed = await confirmScale(preview, request, token);
            token.ThrowIfCancellationRequested();
            if (!confirmed || !preview.HasScalableTargets) return;
            await SetActivityAsync(K8SInventoryActivity.Executing, request.Namespace, request.ResourceType, token, request.Names);
            var response = request.Scope == K8SOperationScope.Namespace
                ? await facade.ScaleNamespaceAsync(request.Namespace, operation, token)
                : await facade.ScaleResourcesAsync(request.Namespace, request.ResourceType, request.Names, operation, token);
            token.ThrowIfCancellationRequested();
            LatestOperation = response.IsFailed(out var commandError, out var result)
                ? new(request, DateTimeOffset.UtcNow, null, null, commandError.Message ?? string.Empty)
                : new(request, result.CompletedAtUtc, null, result, null);
        }
        await PublishAsync();
        token.ThrowIfCancellationRequested();
        if (LatestOperation!.Failure is null) await ReadListCoreAsync(token);
    });

    private async Task RefreshContextCoreAsync(CancellationToken token)
    {
        await SetActivityAsync(K8SInventoryActivity.Reading, Namespace, ResourceType, token);
        var configResponse = await facade.GetRuntimeConfigAsync(token);
        token.ThrowIfCancellationRequested();
        if (configResponse.IsFailed(out var configError, out var config)) ContextFailure = configError.Message ?? string.Empty;
        else { ExecutionNode = config.ExecutionNode; ContextFailure = null; }
        var namespaceResponse = await facade.ListNamespacesAsync(token);
        token.ThrowIfCancellationRequested();
        if (namespaceResponse.IsFailed(out var error, out var namespaces))
        {
            ContextFailure = error.Message ?? string.Empty;
            return;
        }
        Namespaces = namespaces;
        if (string.IsNullOrEmpty(Namespace) && !navigation.HasContext)
            Namespace = namespaces.FirstOrDefault(x => x.Exists)?.Name ?? string.Empty;
        RememberContext();
        await ReadListCoreAsync(token);
    }

    private async Task ReadListCoreAsync(CancellationToken token)
    {
        ReadFailure = null;
        if (string.IsNullOrWhiteSpace(Namespace) || !NamespaceExists) return;
        var namespaceName = Namespace;
        var type = ResourceType;
        await SetActivityAsync(K8SInventoryActivity.Reading, namespaceName, type, token);
        var response = await facade.ListNamespaceInventoryAsync(namespaceName, token);
        token.ThrowIfCancellationRequested();
        if (response.IsFailed(out var error, out var list)) ReadFailure = new(namespaceName, type, error.Message ?? string.Empty);
        else
        {
            NamespaceCapture = list;
            Capture = list.ForResourceType(type);
            _selected.IntersectWith(Capture.Items.Select(K8SResourceIdentity.From));
        }
    }

    private Task AdoptContextCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ContextMatchesNamespaceCapture) return ReadListCoreAsync(token);
        Capture = NamespaceCapture!.ForResourceType(ResourceType);
        return Task.CompletedTask;
    }

    private void RememberContext()
    {
        navigation.Namespace = Namespace;
        navigation.ResourceType = ResourceType;
        navigation.Search = Search;
        navigation.HasContext = true;
    }

    private async Task SetActivityAsync(K8SInventoryActivity activity, string namespaceName,
        K8SResourceType kind, CancellationToken token, IReadOnlyList<string>? names = null)
    {
        token.ThrowIfCancellationRequested();
        Activity = activity;
        ActivityIdentity = string.IsNullOrEmpty(namespaceName) ? string.Empty :
            $"{namespaceName} / {kind}" + (names is { Count: > 0 } ? $" / {string.Join(", ", names)}" : string.Empty);
        await PublishAsync();
        token.ThrowIfCancellationRequested();
    }

    protected override void CompleteOperation()
    {
        Activity = K8SInventoryActivity.None;
        ActivityIdentity = string.Empty;
    }
}
