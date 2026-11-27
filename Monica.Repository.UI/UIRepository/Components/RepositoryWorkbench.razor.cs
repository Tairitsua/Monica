using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Primitives;
using Microsoft.JSInterop;
using Monica.Repository.Persistence.Models;
using Monica.Repository.UI.Localization;
using Monica.Repository.UI.Pages;
using Monica.Repository.UI.UIRepository.State;
using Monica.Repository.UI.UIRepository.Support;
using Monica.UI.Icons;
using Monica.UI.Shared.Components.Visual;
using Monica.UI.Shared.Services;
using MudBlazor;

namespace Monica.Repository.UI.UIRepository.Components;

public partial class RepositoryWorkbench : IAsyncDisposable
{
    [Parameter, EditorRequired] public string View { get; set; } = "Overview";
    [Inject] internal RepositoryDashboardState State { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IHostEnvironment Host { get; set; } = null!;
    [Inject] private MoTimeFormatter UiTime { get; set; } = null!;
    [Inject] private IStringLocalizer<RepositoryUIResource> L { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private IDialogService Dialogs { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private Task? _operation;
    private Task? _navigation;
    private Task? _afterRender;
    private Task? _retirement;
    private IDialogReference? _confirmation;
    private IJSObjectReference? _module;
    private ElementReference _root;
    private IReadOnlyDictionary<string, StringValues> _query = new Dictionary<string, StringValues>();
    private string? _preparedContext;
    private bool _prepared, _busy, _retired, _revealOptions;

    public RepositoryWorkbench() => _token = _lifetime.Token;

    internal bool Busy => _busy;
    internal int Attention => State.Contexts.Count(context => RepositoryPresentation.Standing(context) != "healthy");
    internal int IncompleteCount => State.Contexts.Count(RepositoryPresentation.Incomplete);
    internal int KnownPending => State.Contexts.Sum(context => context.PendingMigrationCount ?? 0);
    internal string PendingDisplay => KnownPending.ToString() + (IncompleteCount > 0 ? "+" : "");
    private string? RouteContext => Get("context") ?? State.SelectedContext?.Registration.ContextId;
    private MoVisualRole RailStandingRole => State.Contexts.Any(context => !context.CanConnect)
        ? MoVisualRole.Error : Attention > 0 ? MoVisualRole.Warning : MoVisualRole.Success;

    private string ViewIcon => View switch
    {
        "Activity" => MoIcons.ChartTrend,
        "Migrations" => MoIcons.Workflow,
        _ => MoIcons.Database
    };

    private string TitleKey => View switch
    {
        "Activity" => "Workbench:Views:Activity:Title",
        "Migrations" => "Workbench:Views:Migrations:Title",
        _ => "Workbench:Views:Overview:Title"
    };

    private string Path => View switch
    {
        "Activity" => UIRepositoryActivityPage.PAGE_URL,
        "Migrations" => UIRepositoryMigrationsPage.PAGE_URL,
        _ => UIRepositoryDashboardPage.PAGE_URL
    };

    private string? Current(string view) => View == view ? "page" : null;

    internal string? Get(string key) => _query.TryGetValue(key, out var value) && value.Count > 0 && value[0] is { Length: > 0 } parameter ? parameter : null;

    internal string Url(params (string Key, string? Value)[] changes)
    {
        var query = _query.ToDictionary(item => item.Key, item => (string?)item.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in changes) query[key] = value;
        return RepositoryRoute.For(Path, query.Select(item => (item.Key, item.Value)).ToArray());
    }

    internal void Replace(params (string Key, string? Value)[] changes) => Navigation.NavigateTo(Url(changes), replace: true);

    internal void Toggle(string key, string? value) => Replace((key, Get(key) == value ? null : value));

    internal void Select(string? id) => Navigation.NavigateTo(Url(("context", id), ("inspectSession", null)));

    internal Task RefreshAsync() => RunAsync(token => PrepareAsync(token, refresh: true));

    internal Task ToggleRevealAsync() => RunAsync(token => PrepareAsync(token, reveal: !State.RevealConnectionStrings));

    internal Task CloseContextAsync()
    {
        Replace(("inspectContext", null), ("tab", null));
        return Task.CompletedTask;
    }

    internal Task CloseActivityOverlayAsync()
    {
        Replace(("inspectSession", null), ("guide", null));
        return Task.CompletedTask;
    }

    protected override void OnInitialized() => Navigation.LocationChanged += OnLocationChanged;

    protected override Task OnParametersSetAsync() => ReadLocationAsync(Navigation.Uri);

    private void OnLocationChanged(object? sender, LocationChangedEventArgs change)
    {
        if (_retired || !string.Equals(Navigation.ToAbsoluteUri(change.Location).AbsolutePath.TrimEnd('/'), Path, StringComparison.OrdinalIgnoreCase))
            return;
        _navigation = InvokeAsync(() => ReadLocationAsync(change.Location));
    }

    private async Task ReadLocationAsync(string href)
    {
        var query = QueryHelpers.ParseQuery(Navigation.ToAbsoluteUri(href).Query);
        if (query.TryGetValue("section", out var section) && section == "options" && Get("section") != "options")
            _revealOptions = true;
        _query = query;
        if (_operation is { IsCompleted: false } active) await active;
        if (_retired) return;
        var requested = View == "Overview" ? null : Get("context");
        if (!_prepared || !string.Equals(_preparedContext, requested, StringComparison.Ordinal))
        {
            _prepared = true;
            _preparedContext = requested;
            await RunAsync(token => PrepareAsync(token));
        }
        if (!_retired) StateHasChanged();
    }

    private Task PrepareAsync(CancellationToken token, bool refresh = false, bool? reveal = null) =>
        State.PrepareWorkspaceAsync(View == "Overview" ? null : Get("context"), View == "Activity", refresh, reveal, token);

    private Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (_busy || _retired) return Task.CompletedTask;
        _busy = true;
        return _operation = RunCoreAsync(work);
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> work)
    {
        try
        {
            StateHasChanged();
            await work(_token);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally
        {
            if (!_retired)
            {
                _busy = false;
                StateHasChanged();
            }
        }
    }

    internal Task ConfirmUpdateAsync(RepositoryDbContextSnapshot selected, bool all) => RunAsync(async token =>
    {
        if (!State.MigrationUpdatesEnabled) return;
        var targets = all ? State.Contexts : [selected];
        var title = L[all ? "Migrations:Confirm:AllTitle" : "Migrations:Confirm:SelectedTitle"];
        var parameters = new DialogParameters<RepositoryMigrationConfirmation>
        {
            { dialog => dialog.All, all },
            { dialog => dialog.Targets, targets },
            { dialog => dialog.Timeout, State.MigrationCommandTimeout },
        };
        var dialog = await Dialogs.ShowAsync<RepositoryMigrationConfirmation>(title, parameters,
            new DialogOptions { CloseOnEscapeKey = true, BackdropClick = false, FullWidth = true, MaxWidth = MaxWidth.Small });
        if (_retired) { Cancel(dialog); return; }
        _confirmation = dialog;
        var answer = await dialog.Result;
        _confirmation = null;
        if (_retired || answer is null || answer.Canceled) return;

        IReadOnlyList<RepositoryMigrationUpdateResult> outcomes;
        if (all)
        {
            outcomes = await State.UpdateAllPendingMigrationsAsync(refreshDetails: false, token);
        }
        else
        {
            var outcome = await State.UpdateMigrationAsync(selected.Registration.ContextId, refreshDetails: false, token);
            outcomes = outcome is null ? [] : [outcome];
        }

        if (_retired) return;
        if (State.MigrationCommandError is not null)
        {
            Snackbar.Add(L["Migrations:Messages:UpdateFailed"], Severity.Error);
            return;
        }
        var failed = outcomes.Count(result => !result.Succeeded);
        var skipped = outcomes.Count(result => result.Skipped);
        Snackbar.Add(L["Workbench:BulkOutcome", outcomes.Count - failed - skipped, skipped, failed],
            failed > 0 ? Severity.Warning : Severity.Success);
    });

    protected override Task OnAfterRenderAsync(bool firstRender) => _afterRender = BindAsync(_afterRender, firstRender);

    private async Task BindAsync(Task? previous, bool firstRender)
    {
        if (previous is not null) await previous;
        if (_retired) return;
        try
        {
            if (firstRender)
            {
                _module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Monica.Repository.UI/js/repository-workbench.js");
                if (!_retired) await _module.InvokeVoidAsync("bind", _root);
            }
            if (!_retired && _revealOptions && _module is not null)
                _revealOptions = !await _module.InvokeAsync<bool>("revealOptions", _root);
        }
        catch (JSDisconnectedException) { }
    }

    private static void Cancel(IDialogReference dialog)
    {
        var result = DialogResult.Cancel();
        dialog.Close(result);
        dialog.Dismiss(result);
    }

    public ValueTask DisposeAsync() => new(_retirement ??= RetireAsync());

    private async Task RetireAsync()
    {
        _retired = true;
        Navigation.LocationChanged -= OnLocationChanged;
        var failures = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        try
        {
            await ObserveAsync(_lifetime.CancelAsync());
            if (_confirmation is { } dialog)
            {
                try { Cancel(dialog); }
                catch (Exception exception) { failures.Add(exception); }
            }
            await ObserveAsync(_operation);
            await ObserveAsync(_navigation);
            await ObserveAsync(_afterRender);
            if (_module is not null) await ObserveAsync(_module.DisposeAsync().AsTask());
        }
        finally { _lifetime.Dispose(); }

        if (failures.Count > 0) throw new AggregateException("Repository workbench retirement failed.", failures);

        async Task ObserveAsync(Task? task)
        {
            if (task is null) return;
            try { await task; }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (JSDisconnectedException) { }
            catch (Exception exception) { failures.Add(exception); }
        }
    }
}
