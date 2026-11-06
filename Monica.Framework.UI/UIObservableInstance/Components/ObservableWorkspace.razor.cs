using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Monica.Core.ObservableInstance.Models;
using Monica.Core.Results;
using Monica.Framework.UI.UIObservableInstance.Dialogs;
using Monica.Framework.UI.UIObservableInstance.Models;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.Framework.UI.UIObservableInstance.Components;

public partial class ObservableWorkspace
{
    public const string PAGE_URL = "/observable-instance-monitor";
    public const string INSTANCES_URL = PAGE_URL + "/instances";
    public const string GROUPS_URL = PAGE_URL + "/groups";
    public const string EXCEPTIONS_URL = PAGE_URL + "/exceptions";
    private static readonly (string View, string Path, string Icon)[] Routes =
    [
        ("Overview", PAGE_URL, MoIcons.Dashboard),
        ("Instances", INSTANCES_URL, MoIcons.Package),
        ("Groups", GROUPS_URL, MoIcons.Workspace),
        ("Exceptions", EXCEPTIONS_URL, MoIcons.Error)
    ];
    private static readonly HealthState[] HealthStates = [HealthState.Unhealthy, HealthState.Healthy, HealthState.Unknown];
    private readonly object _ownership = new();
    private readonly CancellationTokenSource _retirement = new();
    private readonly HashSet<Task> _operations = [];
    private readonly HashSet<IDialogReference> _dialogs = [];
    private Task? _disposeTask;
    private bool _disposed;
    private bool _busy;
    private string? _error;
    private int _exceptionPage;
    private string? _exceptionFilter;

    /// <summary>Gets or sets the route-owned Overview, Instances, Groups, or Exceptions view.</summary>
    [Parameter, EditorRequired] public string View { get; set; } = "Overview";
    private string Url(string path) => Navigation.ToAbsoluteUri(path.TrimStart('/')).ToString();
    private IReadOnlyList<ObservableInstanceSnapshot> Instances => Workspace.Capture?.Instances ?? [];
    private IReadOnlyList<IGrouping<string?, ObservableInstanceSnapshot>> Groups => Instances.GroupBy(i => string.IsNullOrEmpty(i.GroupId) ? null : i.GroupId).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
    private IReadOnlyList<IGrouping<string?, ObservableInstanceSnapshot>> Types => Instances.GroupBy(i => i.InstanceType?.AssemblyQualifiedName).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
    private static string GroupValue(string? group) => group is null ? "ungrouped" : "group:" + group;
    private static string TypeValue(string? type) => type is null ? "untyped" : "type:" + type;
    private string GroupLabel(string? group) => group ?? L["Shared:Labels:Ungrouped"].Value;
    private string SelectedGroupLabel => Workspace.Group.Length == 0 ? L["Workbench:AllGroups"].Value : GroupLabel(Workspace.Group == "ungrouped" ? null : Workspace.Group[6..]);
    private int CountHealth(HealthState health) => Instances.Count(i => ObservableInstanceCapture.Health(i) == health);
    private bool MatchScope(ObservableInstanceSnapshot instance)
    {
        if (Workspace.Status.Length != 0 && Workspace.Status != ObservableInstanceCapture.Health(instance).ToString()) return false;
        if (Workspace.Group.Length != 0 && Workspace.Group != GroupValue(string.IsNullOrEmpty(instance.GroupId) ? null : instance.GroupId)) return false;
        if (Workspace.Type.Length != 0 && Workspace.Type != TypeValue(instance.InstanceType?.AssemblyQualifiedName)) return false;
        if (Workspace.ExceptionsOnly && !instance.History.Any(e => e.Exception is not null)) return false;
        if (Workspace.CriticalErrorOnly && instance.CurrentLogLevel is not (LogLevel.Critical or LogLevel.Error)) return false;
        if (Workspace.Levels.Count > 0 && (instance.CurrentLogLevel is not { } level || !Workspace.Levels.Contains(level))) return false;
        if (ParseDate(Workspace.From) is { } from && instance.RegisteredAt < from) return false;
        if (ParseDate(Workspace.To) is { } to && instance.RegisteredAt > to) return false;
        return true;
    }
    private bool MatchText(ObservableInstanceSnapshot instance) => string.IsNullOrWhiteSpace(Workspace.Search)
        || Contains(instance.Identity.InstanceId) || Contains(instance.InstanceName) || Contains(instance.InstanceTypeName)
        || Contains(instance.InstanceKey) || Contains(instance.GroupId) || Contains(instance.CurrentState.Text);
    private bool Contains(string? value) => value?.Contains(Workspace.Search.Trim(), StringComparison.OrdinalIgnoreCase) == true;
    private static DateTime? ParseDate(string text) => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value) ? value.ToUniversalTime() : null;
    private IReadOnlyList<ObservableInstanceSnapshot> Matches => Instances.Where(i => MatchScope(i) && MatchText(i)).OrderByDescending(i => i.RegisteredAt).ThenBy(i => i.Identity.InstanceId, StringComparer.Ordinal).ToArray();
    private IReadOnlyList<ExceptionEvidence> ExceptionEntries
    {
        get
        {
            var signature = string.Join('|', Workspace.Search, Workspace.Status, Workspace.Group, Workspace.Type, string.Join(',', Workspace.Levels.Order()), Workspace.CriticalErrorOnly, Workspace.From, Workspace.To, Workspace.ExceptionsOnly, Workspace.Capture?.ReadAt);
            if (_exceptionFilter != signature) { _exceptionFilter = signature; _exceptionPage = 0; }
            return Instances.Where(MatchScope).SelectMany(i => i.History.Where(e => e.Exception is not null &&
                (MatchText(i) || Contains(e.Message) || Contains(e.Exception.Message) || Contains(e.Exception.TypeName) || Contains(e.Exception.Details)))
                .Select(e => new ExceptionEvidence(i, e)))
                .OrderByDescending(e => e.Entry.Timestamp).ThenBy(e => e.Instance.Identity.InstanceId, StringComparer.Ordinal).ThenByDescending(e => e.Entry.Sequence).ToArray();
        }
    }
    private int ExceptionPageCount => Math.Max(1, (ExceptionEntries.Count + 11) / 12);
    private int ExceptionPage => Math.Min(_exceptionPage, ExceptionPageCount - 1);

    protected override Task OnInitializedAsync() => Workspace.Capture is null ? RefreshAsync() : Task.CompletedTask;
    private void ResetFilters() { Workspace.ResetFilters(); _exceptionPage = 0; }
    private void SelectLevel(LogLevel level, bool selected) { if (selected) Workspace.Levels.Add(level); else Workspace.Levels.Remove(level); }
    private void OpenHealth(HealthState health) { ResetFilters(); Workspace.Status = health.ToString(); Navigation.NavigateTo(Url(INSTANCES_URL)); }
    private void OpenGroup(string? group) { ResetFilters(); Workspace.Group = GroupValue(group); Navigation.NavigateTo(Url(GROUPS_URL)); }
    private void OpenType(string? type) { ResetFilters(); Workspace.Type = TypeValue(type); Navigation.NavigateTo(Url(INSTANCES_URL)); }

    private Task Admit(Func<Task> action)
    {
        lock (_ownership)
        {
            if (_disposed || _busy) return Task.CompletedTask;
            _busy = true;
            var task = RunAsync(action);
            _operations.Add(task);
            _ = ForgetAsync(task);
            return task;
        }
    }
    private async Task ForgetAsync(Task task)
    {
        try { await task; }
        catch { /* RunAsync projects operational errors into the page. */ }
        finally { lock (_ownership) _operations.Remove(task); }
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { _error = null; await action(); }
        catch (OperationCanceledException) when (_retirement.IsCancellationRequested) { }
        catch (Exception exception) { if (!_disposed) _error = exception.Message; }
        finally { if (!_disposed) { _busy = false; StateHasChanged(); } }
    }
    private Task RefreshAsync() => Admit(ReadAsync);
    private async Task ReadAsync()
    {
        var result = await MonitorService.CaptureAsync(_retirement.Token);
        if (_disposed) return;
        var error = result.WithoutData();
        var capture = result.Data;
        if (result.IsFailure()) _error = error.Message;
        else Workspace.Capture = capture;
    }
    private Task InspectAsync(ObservableInstanceSnapshot instance) => ShowInspector(instance, "Current", null);
    private Task InspectExceptionAsync(ObservableInstanceSnapshot instance, long sequence) => ShowInspector(instance, "Exceptions", sequence);
    private Task ShowInspector(ObservableInstanceSnapshot instance, string section, long? sequence) => Admit(async () =>
    {
        var parameters = new DialogParameters<ObservableSnapshotDialog> { { x => x.Instance, instance }, { x => x.InitialSection, section }, { x => x.SelectedSequence, sequence } };
        var dialog = await DialogService.ShowAsync<ObservableSnapshotDialog>(L["Workbench:InspectorTitle"], parameters,
            new DialogOptions { CloseOnEscapeKey = true, CloseButton = true, MaxWidth = MaxWidth.Large, FullWidth = true });
        await OwnDialogAsync(dialog);
    });
    private async Task<DialogResult?> OwnDialogAsync(IDialogReference dialog)
    {
        lock (_ownership)
        {
            if (_disposed) { dialog.Close(); return null; }
            _dialogs.Add(dialog);
        }
        try { return await dialog.Result.WaitAsync(_retirement.Token); }
        finally
        {
            lock (_ownership) _dialogs.Remove(dialog);
            if (_disposed) dialog.Close();
        }
    }
    private Task ClearAsync(ObservableInstanceSnapshot instance) => Admit(async () =>
    {
        var parameters = new DialogParameters<ObservableClearHistoryDialog> { { x => x.Instance, instance } };
        var dialog = await DialogService.ShowAsync<ObservableClearHistoryDialog>(L["Shared:Actions:ClearHistory"], parameters,
            new DialogOptions { CloseOnEscapeKey = true, CloseButton = true, MaxWidth = MaxWidth.Small, FullWidth = true });
        var confirmation = await OwnDialogAsync(dialog);
        if (_disposed || confirmation is null || confirmation.Canceled) return;
        var result = await MonitorService.ClearHistoryAsync(instance.Identity, _retirement.Token);
        if (_disposed) return;
        var error = result;
        if (result.IsFailure()) _error = error.Message;
        else await ReadAsync();
    });
    public ValueTask DisposeAsync()
    {
        lock (_ownership)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= RetireAsync());
        }
    }
    private async Task RetireAsync()
    {
        await _retirement.CancelAsync();
        Task[] operations;
        lock (_ownership)
        {
            foreach (var dialog in _dialogs.ToArray()) dialog.Close();
            operations = _operations.ToArray();
        }
        try { await Task.WhenAll(operations); }
        catch (OperationCanceledException) when (_retirement.IsCancellationRequested) { }
        _retirement.Dispose();
    }
    private sealed record ExceptionEvidence(ObservableInstanceSnapshot Instance, ObservableStateEntrySnapshot Entry);
}
