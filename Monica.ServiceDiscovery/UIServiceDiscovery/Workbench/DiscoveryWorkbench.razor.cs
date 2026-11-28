using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Monica.Core;
using Monica.Modules;
using Monica.UI.Icons;
using Monica.ServiceDiscovery.Facades;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.UIServiceDiscovery.State;

namespace Monica.ServiceDiscovery.UIServiceDiscovery.Workbench;

internal sealed record DiscoveryContext(DiscoveryWorkbench Owner);

[StreamRendering]
public partial class DiscoveryWorkbench : IAsyncDisposable
{
    internal const string REGISTRY = "service-discovery-manage";
    internal const string DEPENDENCIES = REGISTRY + "/dependencies";
    internal const string DOMAINS = REGISTRY + "/subdomains";
    internal const string HOST = REGISTRY + "/current-host";
    internal const string ENDPOINTS = REGISTRY + "/endpoints";

    private static readonly RouteEntry[] Routes =
    [
        new("Registry", REGISTRY, MoIcons.Cloud), new("Dependencies", DEPENDENCIES, MoIcons.Workflow),
        new("Domains", DOMAINS, MoIcons.Building), new("Host", HOST, MoIcons.Monitor),
        new("Endpoints", ENDPOINTS, MoIcons.Network)
    ];

    private readonly CancellationTokenSource _lifetime = new();
    private ElementReference _root;
    private Task? _keyboard;
    private IJSObjectReference? _keys;
    private Task? _polling;
    private Task? _refresh;
    private bool _busy;
    private bool _disposed;
    private string? _endpointError;
    private ServiceEndpointDiscoveryFacade _endpointFacade = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private ILogger<DiscoveryWorkbench> Logger { get; set; } = default!;
    [Parameter, EditorRequired] public string View { get; set; } = "Registry";

    private ServiceDiscoveryPageState? Membership { get; set; }
    private RouteEntry Current => Routes.Single(route => route.View == View);
    internal string Title => T(View + ":Title");
    private bool CanPoll => View is "Registry" or "Host";
    private bool Auto => Membership?.AutoRefresh == true;
    private bool IsEndpoint => View == "Endpoints";
    private string? Error => IsEndpoint ? _endpointError : Membership?.Error;
    private DateTimeOffset? CapturedAt => IsEndpoint ? Endpoints?.CapturedAt : Evidence?.At is { } at ? new DateTimeOffset(at) : null;
    private string? ExternalStorageKey => Settings is { StorageMode: ServiceDiscoveryStorageMode.ExternalKeyed } settings
        ? settings.ExternalStateStoreServiceKey : null;
    private (string Key, string Role) Posture => (Error, CapturedAt, IsEndpoint, Autonomous, CanPoll && Auto) switch
    {
        (not null, null, _, _, _) => ("failedStatus", "error"),
        (not null, not null, _, _, _) => ("staleStatus", "warning"),
        (_, null, _, _, _) => ("loadingStatus", "info"),
        (_, _, true, _, _) => Endpoints?.Targets.Count == 0 ? ("epEmptyStatus", "neutral") : ("epProcessStatus", "info"),
        (_, _, _, false, _) => ("manualStatus", "neutral"),
        (_, _, _, _, true) => ("live", "info"),
        _ => Evidence?.Services.Count == 0 ? ("emptyStatus", "neutral") : ("paused", "success")
    };

    internal DiscoveryEvidence? Evidence => Membership?.Evidence;
    internal ServiceEndpointDiagnosticsSnapshot? Endpoints { get; private set; }
    internal ModuleServiceDiscoveryOption? Settings { get; private set; }
    internal ModuleServiceDiscoveryUIOption? UiSettings { get; private set; }
    internal bool Autonomous { get; private set; }
    internal bool CanRevoke { get; private set; }
    internal string? Param(string name) => QueryHelpers.ParseQuery(Nav.ToAbsoluteUri(Nav.Uri).Query).GetValueOrDefault(name).FirstOrDefault();
    internal static string Bool(bool value) => value ? "true" : "false";

    internal string Link(string path, params (string Key, string? Value)[] parameters) =>
        QueryHelpers.AddQueryString(Nav.ToAbsoluteUri(path).AbsolutePath,
            parameters.Where(item => !string.IsNullOrEmpty(item.Value))
                .Select(item => new KeyValuePair<string, string?>(item.Key, item.Value)));

    internal string Here(params (string Key, string? Value)[] parameters) =>
        Nav.GetUriWithQueryParameters(parameters.ToDictionary(item => item.Key, item => (object?)item.Value));

    internal void Pick(string key, string? value, bool replace = false) => SetQuery(replace, (key, value));

    internal void SetQuery(bool replace, params (string Key, string? Value)[] parameters)
    {
        if (!_disposed) Nav.NavigateTo(Here(parameters), new NavigationOptions { ReplaceHistoryEntry = replace });
    }

    internal void OpenService(string appId) => SetQuery(false, ("inspect", appId), ("tab", null));

    protected override async Task OnInitializedAsync()
    {
        Membership = Services.GetService<ServiceDiscoveryPageState>();
        if (Membership is not null)
        {
            Settings = Services.GetRequiredService<IOptions<ModuleServiceDiscoveryOption>>().Value;
            UiSettings = Services.GetRequiredService<IOptions<ModuleServiceDiscoveryUIOption>>().Value;
            CanRevoke = Services.GetKeyedService<Monica.Locker.Leases.IExclusiveLeaseStore>(nameof(ModuleServiceDiscovery))
                is Monica.Locker.Leases.ILeaseAdministration;
        }
        Autonomous = Services.GetRequiredService<MonicaApplication>().Application.AutonomousWork;
        _endpointFacade = Services.GetRequiredService<ServiceEndpointDiscoveryFacade>();
        Nav.LocationChanged += OnLocationChanged;
        CaptureEndpoints();
        if (Membership is not null && Evidence is null) await RefreshMembershipAsync();
        if (!_disposed && CanPoll) _polling = PollAsync();
    }

    protected override Task OnAfterRenderAsync(bool firstRender) =>
        firstRender && !_disposed ? _keyboard = AttachKeyboardAsync() : Task.CompletedTask;

    private async Task AttachKeyboardAsync()
    {
        try
        {
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Monica.ServiceDiscovery/UIServiceDiscovery/workbenchKeys.js");
            if (!_disposed) _keys = await module.InvokeAsync<IJSObjectReference>("attach", _root);
        }
        catch (JSDisconnectedException) { }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        if (!_disposed && Nav.ToAbsoluteUri(args.Location).AbsolutePath == Nav.ToAbsoluteUri(Current.Path).AbsolutePath)
            _ = InvokeAsync(StateHasChanged);
    }

    private void ToggleAuto() => Membership!.AutoRefresh = !Auto;

    internal Task RefreshAsync() => StartRefresh(resolve: IsEndpoint, membership: !IsEndpoint);

    private Task RefreshMembershipAsync() => StartRefresh(resolve: false, membership: true);

    private Task StartRefresh(bool resolve, bool membership)
    {
        if (_disposed || _busy) return Task.CompletedTask;
        return _refresh = CaptureAsync(resolve, membership);
    }

    private async Task CaptureAsync(bool resolve, bool membership)
    {
        _busy = true;
        try
        {
            if (resolve) await _endpointFacade.RefreshAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (membership && Membership is not null) await Membership.RefreshAsync(_lifetime.Token);
            if (!membership) CaptureEndpoints();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _busy = false; }
        if (!_disposed) StateHasChanged();
    }

    private void CaptureEndpoints()
    {
        try
        {
            Endpoints = _endpointFacade.Capture().Data!;
            _endpointError = null;
        }
        catch (Exception error)
        {
            Logger.LogError(error, "Failed to capture outbound endpoint diagnostics.");
            _endpointError = T("epReadFailed");
        }
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(ServiceDiscoveryPageState.RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                if (Auto) await InvokeAsync(RefreshMembershipAsync);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private (string Label, string Role)? RailMark(string view)
    {
        if (view == "Endpoints" && Endpoints is { } endpoints)
        {
            var count = endpoints.Targets.Count(target => EndpointAttention(endpoints, target));
            return count == 0 ? ($"{endpoints.Targets.Count}", "neutral") : ($"{count}/{endpoints.Targets.Count}",
                endpoints.Targets.Any(target => EndpointState(endpoints, target) is "Failed" or "Timeout") ? "error" : "warning");
        }
        if (Evidence is not { } snapshot) return null;
        switch (view)
        {
            case "Registry":
                return snapshot.Attention == 0 ? ($"{snapshot.Services.Count}", "neutral") :
                    ($"{snapshot.Attention}/{snapshot.Services.Count}", snapshot.Services.Any(service => service.Offline) ? "error" : "warning");
            case "Dependencies" when snapshot.Topology.IsAvailable:
                var pairs = snapshot.Pairs(false).ToArray();
                var count = pairs.Count(pair => pair.Severity > 0);
                return count == 0 ? ($"{pairs.Count(pair => pair.Remote)}", "primary") :
                    ($"{count}", pairs.Any(pair => pair.Severity == 2) ? "error" : "warning");
            case "Domains":
                var domains = snapshot.Domains.Count(domain => domain.Catalog is not null);
                return snapshot.HostedCatalog == domains && domains == snapshot.Domains.Count ? ($"{domains}", "neutral") :
                    ($"{snapshot.HostedCatalog}/{domains}", "warning");
            case "Host" when snapshot.Agreement == "split": return ("!", "error");
            case "Host" when snapshot.LocalInstance.Health is "Unhealthy" or "Offline": return ($"{Num(snapshot.LocalInstance.Age)}s", "warning");
            case "Host" when snapshot.Host.IsLeader: return ("L", "primary");
            default: return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Nav.LocationChanged -= OnLocationChanged;
        await _lifetime.CancelAsync();
        if (_polling is not null) await _polling;
        if (_refresh is not null) await _refresh;
        if (_keyboard is not null) await _keyboard;
        if (_keys is not null)
        {
            try
            {
                await _keys.InvokeVoidAsync("dispose");
                await _keys.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }
        _lifetime.Dispose();
    }

    private sealed record RouteEntry(string View, string Path, string Icon);
}
