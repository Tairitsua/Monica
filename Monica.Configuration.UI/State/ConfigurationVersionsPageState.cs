using Microsoft.Extensions.Localization;
using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Configuration.UI.Localization;
using Monica.Configuration.UI.Support;
using Monica.Core.Results;
using MudBlazor;

namespace Monica.Configuration.UI.State;

/// <summary>Owns one versions page's bounded saved-snapshot list and explicit comparison pair.</summary>
/// <remarks>
/// Construct a separate instance for each page and await its disposal. Injected services are borrowed.
/// Facade reads are tokenless: retirement rejects late adoption and joins their actual completion rather than
/// claiming cancellation stopped them. Every selection, including a cache hit, supersedes older comparisons.
/// Live callers must observe operation failures; repeated disposal shares the actual retirement completion.
/// </remarks>
internal sealed class ConfigurationVersionsPageState : IAsyncDisposable
{
    private readonly Func<Task<Res<IReadOnlyList<ConfigurationUnifiedVersionSummary>>>> _loadVersions;
    private readonly Func<long, long, Task<Res<ConfigurationUnifiedVersionComparison>>> _compareVersions;
    private readonly Action<string> _reportError;
    private readonly IStringLocalizer<ConfigurationUIResource> _localizer;
    private readonly string _loadFailedText;
    private readonly ConfigurationOperationLifetime _lifetime = new();
    private readonly Dictionary<VersionComparisonCacheKey, ConfigurationUnifiedVersionComparison> _comparisonCache = new();
    private IReadOnlyList<ConfigurationUnifiedVersionSummary> _versions = [];
    private long _comparisonRequestId;
    private long _loadRequestId;
    private ConfigurationVersionComparisonMode _comparisonMode;

    public ConfigurationVersionsPageState(ConfigurationFacade facade, ISnackbar snackbar,
        IStringLocalizer<ConfigurationUIResource> localizer)
        : this(() => facade.GetUnifiedVersionsAsync(limit: 200), facade.CompareUnifiedVersionsAsync,
            message => snackbar.Add(message, Severity.Error), localizer) { }

    internal ConfigurationVersionsPageState(
        Func<Task<Res<IReadOnlyList<ConfigurationUnifiedVersionSummary>>>> loadVersions,
        Func<long, long, Task<Res<ConfigurationUnifiedVersionComparison>>> compareVersions,
        Action<string> reportError, IStringLocalizer<ConfigurationUIResource> localizer)
    {
        _loadVersions = loadVersions;
        _compareVersions = compareVersions;
        _reportError = reportError;
        _localizer = localizer;
        _loadFailedText = localizer["Common:Errors:LoadFailed"];
    }

    public bool Loading { get; private set; } = true;
    public bool ComparisonLoading { get; private set; }
    /// <summary>
    /// Indicates an accepted successful list, including an empty list. Refresh admission retains that evidence
    /// until the newest request succeeds or fails; an accepted failure clears the list and this flag.
    /// </summary>
    public bool HasLoadedVersions { get; private set; }
    public bool HasLoadFailure => ErrorText is not null;
    public string? ErrorText { get; private set; }
    public string? ComparisonError { get; private set; }
    public bool IsDisposed => _lifetime.IsRetired;
    public ConfigurationUnifiedVersionComparison? SelectedComparison { get; private set; }
    public long? SelectedHistoryVersion { get; private set; }
    public long? OriginVersion { get; private set; }
    public long? TargetVersion { get; private set; }

    /// <summary>Gets at most 200 loaded saved snapshots, newest first; this is not the total retained count.</summary>
    public IReadOnlyList<ConfigurationUnifiedVersionSummary> Versions => _versions;

    /// <summary>Gets the latest loaded saved capture, not the current live configuration value.</summary>
    public ConfigurationUnifiedVersionSummary? CurrentVersion => _versions.FirstOrDefault();
    public IReadOnlyList<ConfigurationUnifiedVersionSummary> HistoryVersions => _versions.Skip(1).ToArray();
    public bool HasVersions => _versions.Count > 0;
    public bool HasSelectedHistoryVersion => SelectedHistoryVersion is { } selected
        && _versions.Any(version => version.Version == selected);
    public string CurrentVersionLabel => CurrentVersion is { } current
        ? ConfigurationUnifiedVersionJsonFormatter.VersionLabel(current.Version) : _localizer["Common:States:Empty"];
    public long? PreviousComparisonVersion => _comparisonMode == ConfigurationVersionComparisonMode.Previous
        ? SelectedHistoryVersion : null;
    public string ComparisonTitle => OriginVersion is { } origin && TargetVersion is { } target
        ? _comparisonMode == ConfigurationVersionComparisonMode.Explicit
            ? _localizer["Versions:Diff:Title", ConfigurationUnifiedVersionJsonFormatter.VersionLabel(origin),
                ConfigurationUnifiedVersionJsonFormatter.VersionLabel(target)]
            : _comparisonMode == ConfigurationVersionComparisonMode.Previous
            ? _localizer["Versions:Compare:AgainstPrevious", VersionText(origin), VersionText(target)]
            : _localizer["Versions:Compare:AgainstCurrent", VersionText(origin), VersionText(target)]
        : _localizer["Versions:Compare:NoSelection"];
    public string ComparisonSubtitle => _comparisonMode == ConfigurationVersionComparisonMode.Previous
        ? _localizer["Versions:Compare:PreviousPair"] : _localizer["Versions:Compare:CurrentOnly"];

    /// <summary>
    /// Reloads the bounded list and compares the retained selection, or the newest available pair.
    /// The accepted list remains available while refreshing; only the newest request can replace or invalidate it.
    /// </summary>
    public Task LoadAsync() => _lifetime.RunAsync(async _ =>
    {
        long requestId = 0;
        _lifetime.TryApply(() =>
        {
            requestId = ++_loadRequestId;
            ++_comparisonRequestId;
            Loading = true;
            ComparisonLoading = false;
            SelectedComparison = null;
            ComparisonError = null;
            ErrorText = null;
            _comparisonCache.Clear();
        });
        Res<IReadOnlyList<ConfigurationUnifiedVersionSummary>> result;
        try { result = await _loadVersions(); }
        catch (Exception exception) { result = Res.Fail(exception.Message); }

        string? failure = null;
        long? origin = null;
        long? target = null;
        var mode = ConfigurationVersionComparisonMode.Current;
        _lifetime.TryApply(() =>
        {
            if (requestId != _loadRequestId) return;
            Loading = false;
            if (result.IsFailed(out var error, out var versions))
            {
                failure = ErrorText = error.Message ?? _loadFailedText;
                _versions = [];
                HasLoadedVersions = false;
                ResetSelection();
                return;
            }
            _versions = Array.AsReadOnly(versions.OrderByDescending(static version => version.Version).ToArray());
            HasLoadedVersions = true;
            if (CurrentVersion is not { } current) { ResetSelection(); return; }
            var selected = SelectedHistoryVersion is { } selectedVersion
                ? _versions.FirstOrDefault(version => version.Version == selectedVersion) : null;
            selected ??= _versions.Skip(1).FirstOrDefault() ?? current;
            if (_comparisonMode == ConfigurationVersionComparisonMode.Explicit
                && OriginVersion is { } retainedOrigin && TargetVersion is { } retainedTarget
                && _versions.Any(version => version.Version == retainedOrigin)
                && _versions.Any(version => version.Version == retainedTarget))
            {
                origin = retainedOrigin;
                target = retainedTarget;
                mode = ConfigurationVersionComparisonMode.Explicit;
            }
            else if (_comparisonMode == ConfigurationVersionComparisonMode.Previous && PreviousVersion(selected) is { } previous)
            {
                origin = previous.Version;
                target = selected.Version;
                mode = ConfigurationVersionComparisonMode.Previous;
            }
            else
            {
                origin = selected.Version;
                target = current.Version;
            }
        });
        ReportFailure(failure);
        if (origin is { } originVersion && target is { } targetVersion && !IsDisposed && requestId == _loadRequestId)
            await LoadComparisonAsync(originVersion, targetVersion,
                mode == ConfigurationVersionComparisonMode.Previous ? targetVersion : originVersion, mode);
    });

    /// <summary>Compares loaded saved snapshots, including equal or reversed pairs.</summary>
    public Task SelectComparisonAsync(long originVersion, long targetVersion) =>
        LoadComparisonAsync(originVersion, targetVersion, originVersion, ConfigurationVersionComparisonMode.Explicit);

    /// <summary>Compares a ledger selection with the latest saved snapshot, including an equal latest pair.</summary>
    public Task SelectVersionAsync(long version) => CurrentVersion is { } current
        ? LoadComparisonAsync(version, current.Version, version, ConfigurationVersionComparisonMode.Current)
        : ClearSelectionAsync();

    public Task CompareWithPreviousAsync(ConfigurationUnifiedVersionSummary version) => PreviousVersion(version) is { } previous
        ? LoadComparisonAsync(previous.Version, version.Version, version.Version, ConfigurationVersionComparisonMode.Previous)
        : ClearSelectionAsync();

    private Task ClearSelectionAsync() => _lifetime.RunAsync(_ =>
    {
        _lifetime.TryApply(ResetSelection);
        return Task.CompletedTask;
    });

    private Task LoadComparisonAsync(long originVersion, long targetVersion, long selectedHistoryVersion,
        ConfigurationVersionComparisonMode mode) => _lifetime.RunAsync(async _ =>
    {
        var key = new VersionComparisonCacheKey(originVersion, targetVersion);
        long requestId = 0;
        var needsLoad = false;
        string? failure = null;
        _lifetime.TryApply(() =>
        {
            requestId = ++_comparisonRequestId;
            SelectedHistoryVersion = selectedHistoryVersion;
            OriginVersion = originVersion;
            TargetVersion = targetVersion;
            _comparisonMode = mode;
            SelectedComparison = null;
            ComparisonLoading = false;
            ComparisonError = null;
            if (!_versions.Any(version => version.Version == originVersion)
                || !_versions.Any(version => version.Version == targetVersion))
            {
                failure = ComparisonError = _loadFailedText;
                return;
            }
            if (_comparisonCache.TryGetValue(key, out var cached)) SelectedComparison = cached;
            else { ComparisonLoading = true; needsLoad = true; }
        });
        ReportFailure(failure);
        if (!needsLoad) return;

        Res<ConfigurationUnifiedVersionComparison> result;
        try { result = await _compareVersions(originVersion, targetVersion); }
        catch (Exception exception) { result = Res.Fail(exception.Message); }
        failure = null;
        _lifetime.TryApply(() =>
        {
            if (requestId != _comparisonRequestId) return;
            ComparisonLoading = false;
            if (result.IsFailed(out var error, out var comparison))
            {
                failure = ComparisonError = error.Message ?? _loadFailedText;
                return;
            }
            _comparisonCache[key] = comparison;
            SelectedComparison = comparison;
        });
        ReportFailure(failure);
    });

    private void ResetSelection()
    {
        ++_comparisonRequestId;
        SelectedHistoryVersion = OriginVersion = TargetVersion = null;
        SelectedComparison = null;
        ComparisonLoading = false;
        ComparisonError = null;
    }

    private void ReportFailure(string? failure)
    {
        if (failure is not null && !IsDisposed) _reportError($"{_loadFailedText}: {failure}");
    }

    private static string VersionText(long version) => ConfigurationUnifiedVersionJsonFormatter.VersionLabel(version);

    private ConfigurationUnifiedVersionSummary? PreviousVersion(ConfigurationUnifiedVersionSummary version)
    {
        var index = _versions.ToList().FindIndex(candidate => candidate.Version == version.Version);
        return index >= 0 && index + 1 < _versions.Count ? _versions[index + 1] : null;
    }

    public ValueTask DisposeAsync() => _lifetime.RetireAsync();
    private enum ConfigurationVersionComparisonMode { Current, Previous, Explicit }
    private readonly record struct VersionComparisonCacheKey(long OriginVersion, long TargetVersion);
}
