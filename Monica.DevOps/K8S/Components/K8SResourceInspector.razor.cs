using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Monica.Core.Results;
using Monica.DevOps.K8S.Dialogs;
using Monica.DevOps.K8S.Facades;
using Monica.DevOps.K8S.Models;
using Monica.DevOps.K8S.Support;
using Monica.DevOps.Localization;
using Monica.UI.Shared.Components.Visual;
using Monica.UI.Shared.Services;
using MudBlazor;

namespace Monica.DevOps.K8S.Components;

/// <summary>Shows captured Kubernetes evidence in a temporary, keyboard-contained inspector.</summary>
/// <remarks>The parent owns resource reads, operation review and open state. Keep this component mounted
/// while navigating relations, then retire it when closing or changing namespace. The shared side sheet
/// owns native dialog focus and dismissal; callers must preserve their launch controls.</remarks>
public partial class K8SResourceInspector : IAsyncDisposable
{
    [Inject] private IStringLocalizer<K8SResource> L { get; set; } = default!;
    [Inject] private K8SFacade K8SFacade { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private MoTimeFormatter UiTime { get; set; } = default!;

    /// <summary>Gets or sets captured resource, Pod and raw Describe evidence.</summary>
    [Parameter, EditorRequired] public K8SResourceDetails Details { get; set; } = new();
    /// <summary>Gets or sets whether the shared evidence sheet is open.</summary>
    [Parameter] public bool Open { get; set; }
    /// <summary>Gets or sets the shared dismissal callback used by Close, Escape and the overlay.</summary>
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }
    /// <summary>Gets or sets when the displayed evidence was captured; omit when unavailable.</summary>
    [Parameter] public DateTimeOffset? CapturedAt { get; set; }
    /// <summary>Gets or sets the originating visit lifetime, linked to owned Pod reads.</summary>
    [Parameter] public CancellationToken LifetimeToken { get; set; }
    /// <summary>Gets or sets whether this captured identity is eligible for operation requests.</summary>
    [Parameter] public bool CanOperate { get; set; }
    /// <summary>Gets or sets whether the parent is reading, reviewing or executing.</summary>
    [Parameter] public bool IsBusy { get; set; }
    /// <summary>Gets or sets Services resolved from complete captured namespace evidence.</summary>
    [Parameter] public IReadOnlyList<K8SResourceSummary> RelatedServices { get; set; } = [];
    /// <summary>Gets or sets whether Service relationship coverage is available.</summary>
    [Parameter] public bool ShowServiceRelations { get; set; }
    /// <summary>Gets or sets the callback that reads a selected related resource.</summary>
    [Parameter] public EventCallback<K8SResourceSummary> OnInspectRelated { get; set; }
    /// <summary>Gets or sets the callback that reviews a single-resource restart request.</summary>
    [Parameter] public EventCallback<K8SResourceSummary> OnRestart { get; set; }
    /// <summary>Gets or sets the callback that reviews a single-resource scale-to-zero request.</summary>
    [Parameter] public EventCallback<K8SResourceSummary> OnScaleDown { get; set; }
    /// <summary>Gets or sets the callback that reviews a single-resource replica-restoration request.</summary>
    [Parameter] public EventCallback<K8SResourceSummary> OnScaleUp { get; set; }

    private readonly object _sync = new();
    private readonly string _titleId = $"k8s-inspector-{Guid.NewGuid():N}";
    private CancellationTokenSource? _lifetime;
    private CancellationToken _token;
    private K8SDialogOwner? _nested;
    private Task _podWork = Task.CompletedTask;
    private Task _actionWork = Task.CompletedTask;
    private Task? _retirement;
    private volatile bool _retired;
    private string? _pendingPod;
    private string? _operationOrigin;
    private string? _displayedIdentity;
    private int _activeTab;

    private K8SResourceSummary Summary => Details.Summary;
    private bool IsService => Summary.ResourceType == K8SResourceType.Service;
    private bool HasPendingAction => !_actionWork.IsCompleted;
    private bool HasPendingWork => !_podWork.IsCompleted || !_actionWork.IsCompleted;
    private IReadOnlyList<K8SWorkloadReference> AttentionWorkloads => IsService
        ? Details.RelatedWorkloads.Where(x => x.DesiredReplicas > 0 && x.ReadyReplicas < x.DesiredReplicas).ToArray()
        : Summary.DesiredReplicas > 0 && Summary.ReadyReplicas < Summary.DesiredReplicas
            ? [new() { Name = Summary.Name, ReadyReplicas = Summary.ReadyReplicas, DesiredReplicas = Summary.DesiredReplicas }]
            : [];
    private MoVisualRole AttentionRole => AttentionWorkloads.Any(x => x.ReadyReplicas == 0) ? MoVisualRole.Error : MoVisualRole.Warning;
    private string PodsTabCaption => Details.RelatedPods.Any(x => x.HasIssue)
        ? L["Inspector:PodsWithAttention", Details.RelatedPods.Count, Details.RelatedPods.Count(x => x.HasIssue)].Value
        : L["Inspector:PodsWithCount", Details.RelatedPods.Count].Value;

    private IReadOnlyList<MoFact> SummaryFacts => IsService
        ? [new(L["DescribeDialog:ServiceType"], DisplayValue(Summary.Type)),
           new(L["DescribeDialog:ClusterIp"], DisplayValue(Summary.ClusterIp)),
           new(L["Inspector:PortsNodePorts"], $"{Summary.Ports.Count} / {Summary.Ports.Count(x => x.NodePort.HasValue)}"),
           new(L["DescribeDialog:BackingWorkloadRestart"], UiTime.Format(Summary.LastRestartedAtUtc))]
        : [new(L["DescribeDialog:ReadyDesired"], $"{Summary.ReadyReplicas} / {Summary.DesiredReplicas}"),
           new(L["Inspector:State"], L[Summary.DesiredReplicas == 0 ? "Inspector:Stopped" : Summary.ReadyReplicas >= Summary.DesiredReplicas ? "Inspector:Ready" : Summary.ReadyReplicas == 0 ? "Inspector:Unavailable" : "Inspector:Degraded"]),
           new(L["DescribeDialog:LastRestart"], UiTime.Format(Summary.LastRestartedAtUtc)),
           ShowServiceRelations ? new(L["Inspector:RelatedServices"], RelatedServices.Count.ToString()) : new(L["DescribeDialog:Images"], Summary.Images.Count.ToString())];

    protected override void OnInitialized()
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken);
        _token = _lifetime.Token;
        _nested = new(DialogService, action => InvokeAsync(action));
    }

    protected override void OnParametersSet()
    {
        var identity = $"{Summary.Namespace}/{Summary.Kind}/{Summary.Name}";
        if (_displayedIdentity == identity) return;
        _displayedIdentity = identity;
        _activeTab = 0;
    }

    private Task DrawerChangedAsync(bool open) => open ? Task.CompletedTask : CloseAsync();
    private Task CloseAsync() => _retired || HasPendingAction ? Task.CompletedTask : OpenChanged.InvokeAsync(false);

    private bool ActionDisabled(string operation) => _retired || _token.IsCancellationRequested ||
        (_operationOrigin != operation && (!CanOperate || IsBusy || HasPendingWork)) ||
        IsService && Details.RelatedWorkloads.Count == 0 || operation != "restart" && Summary.ResourceType == K8SResourceType.DaemonSet;
    private string? ActionAriaDisabled(string operation) => _operationOrigin == operation ? "true" : null;

    private Task RunOperationAsync(string operation, EventCallback<K8SResourceSummary> callback)
    {
        if (ActionDisabled(operation) || HasPendingWork || !callback.HasDelegate) return Task.CompletedTask;
        return RunActionAsync(operation, () => callback.InvokeAsync(Summary));
    }

    private Task InspectWorkloadAsync(K8SWorkloadReference workload) => InspectRelatedAsync(new()
    {
        Name = workload.Name, Namespace = workload.Namespace, Kind = workload.Kind,
        ResourceType = ParseKind(workload.Kind)
    });

    private Task InspectRelatedAsync(K8SResourceSummary resource) => IsBusy || HasPendingWork || _retired || _token.IsCancellationRequested
        ? Task.CompletedTask : RunActionAsync(null, () => OnInspectRelated.InvokeAsync(resource));

    private Task RunActionAsync(string? operation, Func<Task> action)
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_retired || _token.IsCancellationRequested || HasPendingWork) return Task.CompletedTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _actionWork = completion.Task;
            _operationOrigin = operation;
        }
        _ = RunAsync();
        return completion.Task;

        async Task RunAsync()
        {
            try { await action(); completion.TrySetResult(); }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { _operationOrigin = null; }
        }
    }

    private Task OpenPodDescribeAsync(K8SPodSummary pod)
    {
        TaskCompletionSource completion;
        var namespaceName = Summary.Namespace;
        lock (_sync)
        {
            if (_retired || _token.IsCancellationRequested || IsBusy || HasPendingWork) return Task.CompletedTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _podWork = completion.Task;
            _pendingPod = pod.Name;
        }
        _ = ReadAndShowAsync();
        return completion.Task;

        async Task ReadAndShowAsync()
        {
            Exception? failure = null;
            try
            {
                var response = await K8SFacade.DescribePodAsync(namespaceName, pod.Name, _token);
                if (_retired || _token.IsCancellationRequested) return;
                if (response.IsFailed(out var error, out var details))
                {
                    Snackbar.Add(error?.Message ?? L["ServiceMessages:DescribePodFailed", pod.Name, namespaceName, string.Empty].Value, Severity.Error);
                    return;
                }
                var parameters = new DialogParameters<K8SPodDescribeDialog>
                {
                    { x => x.Details, details }, { x => x.ResolvedWorkloadKind, pod.WorkloadKind },
                    { x => x.ResolvedWorkloadName, pod.WorkloadName }, { x => x.CapturedNamespace, namespaceName },
                    { x => x.CapturedPodName, pod.Name }
                };
                await _nested!.ShowAsync<K8SPodDescribeDialog>(pod.Name, parameters,
                    new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseButton = true, CloseOnEscapeKey = true });
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception exception) { failure = exception; }
            finally
            {
                _pendingPod = null;
                if (failure is null) completion.TrySetResult();
                else completion.TrySetException(failure);
            }
        }
    }

    /// <summary>Cancels owned reads, retires nested dialogs and drains actual admitted producers exactly once.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] producers;
        lock (_sync)
        {
            if (_retirement is not null) return new(_retirement);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = completion.Task;
            _retired = true;
            producers = [_podWork, _actionWork];
        }
        _ = DrainAsync();
        return new(completion.Task);

        async Task DrainAsync()
        {
            var failures = new List<Exception>();
            var nested = _nested?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            try
            {
                try { _lifetime?.Cancel(); } catch (Exception exception) { failures.Add(exception); }
                foreach (var task in producers.Append(nested))
                {
                    try { await task; } catch (Exception exception) { failures.Add(exception); }
                }
            }
            finally { _lifetime?.Dispose(); }
            if (failures.Count == 0) completion.TrySetResult();
            else completion.TrySetException(new AggregateException(failures));
        }
    }

    private bool IsLoadingPod(K8SPodSummary pod) => _pendingPod == pod.Name;
    private string DisplayValue(string? value) => string.IsNullOrWhiteSpace(value) ? L["Common:Empty"].Value : value;
    private string LocalizedKind(string kind) => kind is "Service" or "Deployment" or "StatefulSet" or "DaemonSet" ? L[$"ResourceTypes:{kind}"].Value : kind;
    private string PodWorkloadName(K8SPodSummary pod) => !string.IsNullOrWhiteSpace(pod.WorkloadName)
        ? $"{LocalizedKind(pod.WorkloadKind)}/{pod.WorkloadName}"
        : !string.IsNullOrWhiteSpace(pod.OwnerName) ? $"{pod.OwnerKind}/{pod.OwnerName}" : L["Common:Empty"].Value;
    private static K8SResourceType ParseKind(string kind) => K8SResourceTypeExtensions.TryParse(kind, out var type) ? type : K8SResourceType.Deployment;
    private static string KindClass(K8SResourceType type) => type switch
    {
        K8SResourceType.Service => "kind-service", K8SResourceType.StatefulSet => "kind-statefulset",
        K8SResourceType.DaemonSet => "kind-daemonset", _ => "kind-deployment"
    };
    private static Color PodPhaseColor(K8SPodSummary pod) => pod.Phase.ToLowerInvariant() switch
    {
        "running" => Color.Info, "succeeded" => Color.Success, "pending" => Color.Warning,
        "failed" => Color.Error, _ => Color.Default
    };
}
