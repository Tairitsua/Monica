using Microsoft.Extensions.Localization;
using Monica.AI.AgentCapabilities.Models;
using Monica.AI.Facades;
using Monica.AI.Mcp.Facades;
using Monica.AI.Mcp.Models;
using Monica.AI.UI.Localization;
using Monica.AI.UI.Shared.Support;
using Monica.Core.Results;

namespace Monica.AI.UI.UIAgentCapabilities.State;

/// <summary>Component-owned capability evidence and serialized commands; borrowed facades remain host-owned.</summary>
internal sealed class AgentCapabilityAdministrationState(
    AgentCapabilityFacade capabilities,
    McpFacade mcp,
    IStringLocalizer<AIResource> localizer) : IAsyncDisposable
{
    private readonly AIAdministrationOperationLifetime _lifetime = new();
    private string? _selectedKey;
    private long _selectionVersion;

    internal AgentCapabilityManagementInfo? Management { get; private set; }
    internal bool HasLoaded { get; private set; }
    internal bool IsBusy { get; private set; }
    internal bool IsRetired => _lifetime.IsRetired;
    internal string? Error { get; private set; }
    internal AgentCapabilityKind ActiveKind { get; private set; } = AgentCapabilityKind.Skill;
    internal string Query { get; private set; } = string.Empty;
    internal bool? EnabledFilter { get; private set; }
    internal AgentCapabilitySourceKind? SkillSourceFilter { get; private set; }
    internal McpCatalogSourceKind? McpSourceFilter { get; private set; }
    internal CapabilityProbeOutcome? LastProbe { get; private set; }
    internal IReadOnlyList<AgentCapabilityEntryInfo> CurrentEntries => ActiveKind == AgentCapabilityKind.Skill
        ? Management?.Skills ?? [] : Management?.McpEntries ?? [];
    internal AgentCapabilityEntryInfo? SelectedEntry => CurrentEntries.FirstOrDefault(entry => SameKey(entry.Key, _selectedKey));
    internal bool IsCatalogEnabled => ActiveKind == AgentCapabilityKind.Skill
        ? Management?.AreSkillsEnabled == true : Management?.AreMcpToolsEnabled == true;
    internal IReadOnlyList<AgentCapabilityEntryInfo> VisibleEntries => CurrentEntries.Where(entry =>
        (EnabledFilter is null || entry.IsEnabled == EnabledFilter)
        && (ActiveKind != AgentCapabilityKind.Skill || SkillSourceFilter is null || entry.SourceKind == SkillSourceFilter)
        && (ActiveKind != AgentCapabilityKind.Mcp || McpSourceFilter is null || entry.McpSourceKind == McpSourceFilter)
        && (Matches(entry.Name) || Matches(entry.Title) || Matches(entry.Description) || entry.Tools.Any(tool => Matches(tool.Name))))
        .ToArray();

    internal void SetQuery(string? value) => _lifetime.TryApply(() => Query = value ?? string.Empty);
    internal void SetEnabledFilter(bool? value) => _lifetime.TryApply(() => EnabledFilter = value);
    internal void SetSkillSourceFilter(AgentCapabilitySourceKind? value) => _lifetime.TryApply(() => SkillSourceFilter = value);
    internal void SetMcpSourceFilter(McpCatalogSourceKind? value) => _lifetime.TryApply(() => McpSourceFilter = value);
    internal void ClearFilters() => _lifetime.TryApply(() =>
    {
        Query = string.Empty;
        EnabledFilter = null;
        SkillSourceFilter = null;
        McpSourceFilter = null;
    });

    internal void SetActiveKind(AgentCapabilityKind kind) => _lifetime.TryApply(() =>
    {
        if (kind is not (AgentCapabilityKind.Skill or AgentCapabilityKind.Mcp) || kind == ActiveKind) return;
        ActiveKind = kind;
        _selectionVersion++;
        LastProbe = null;
        EnsureSelectedEntry();
    });

    internal void SelectEntry(AgentCapabilityEntryInfo entry) => _lifetime.TryApply(() =>
    {
        if (entry.Kind != ActiveKind || !CurrentEntries.Any(item => SameKey(item.Key, entry.Key)) || SameKey(_selectedKey, entry.Key)) return;
        _selectedKey = entry.Key;
        _selectionVersion++;
        LastProbe = null;
    });

    internal async Task<bool> RefreshAsync() => await ApplyManagementAsync(
        token => capabilities.GetManagementInfoAsync(token)) is null;

    internal Task<string?> SetCatalogEnabledAsync(AgentCapabilityKind kind, bool enabled) =>
        ApplyManagementAsync(token => capabilities.SetCatalogEnabledAsync(kind, enabled, token));

    internal Task<string?> SetEntryEnabledAsync(AgentCapabilityEntryInfo entry, bool enabled) => OwnsEntry(entry)
        ? ApplyManagementAsync(token => capabilities.SetEntryEnabledAsync(entry.Kind, entry.Name, enabled, token))
        : Task.FromResult<string?>(GenericError);

    internal Task<string?> SetSkillMcpServerEnabledAsync(AgentCapabilityEntryInfo entry, bool enabled) =>
        OwnsEntry(entry) && entry.Kind == AgentCapabilityKind.Skill && entry.CanExposeAsMcpServer
            ? ApplyManagementAsync(token => capabilities.SetSkillMcpServerEnabledAsync(entry.Name, enabled, token))
            : Task.FromResult<string?>(GenericError);

    /// <summary>Captures a profile's exact values so an old editor cannot replace refreshed profile settings.</summary>
    internal ExternalMcpProfileEditCapture CaptureExternalProfile(AgentCapabilityEntryInfo? entry) => new(
        this, entry?.Name, entry?.McpExternalProfile is { } profile
            ? profile with { Headers = new(profile.Headers, StringComparer.OrdinalIgnoreCase) } : null);

    /// <summary>Saves a submitted profile; dialog callers should use the captured-origin overload when editing.</summary>
    internal Task<string?> SaveExternalProfileAsync(ExternalMcpClientProfile profile, CancellationToken ct = default) =>
        ApplyManagementAsync(token => mcp.SaveExternalProfileAsync(profile, token), ct, profile.Headers, profile.Name);

    internal Task<string?> SaveExternalProfileAsync(
        ExternalMcpProfileEditCapture capture, ExternalMcpClientProfile profile, CancellationToken ct = default)
    {
        if (!ReferenceEquals(capture.Owner, this) || IsRetired) return Task.FromResult<string?>(GenericError);
        var existing = Management?.McpEntries.FirstOrDefault(entry => SameKey(entry.Name, profile.Name));
        if (capture.Profile is null)
        {
            if (existing is not null) return Task.FromResult<string?>(GenericError);
        }
        else if (!SameKey(capture.Name, profile.Name) || existing is not { IsUserManaged: true, McpExternalProfile: { } current }
                 || !SameProfile(capture.Profile, current))
        {
            return Task.FromResult<string?>(GenericError);
        }
        return SaveExternalProfileAsync(profile, ct);
    }

    internal Task<string?> DeleteExternalProfileAsync(AgentCapabilityEntryInfo entry) =>
        OwnsEntry(entry) && entry.IsUserManaged
            ? ApplyManagementAsync(token => mcp.DeleteExternalProfileAsync(entry.Name, token), headers: entry.McpExternalProfile?.Headers)
            : Task.FromResult<string?>(GenericError);

    internal Task<string?> TestMcpAsync(AgentCapabilityEntryInfo entry)
    {
        if (!OwnsEntry(entry) || entry.Kind != AgentCapabilityKind.Mcp) return Task.FromResult<string?>(GenericError);
        var selectionVersion = _selectionVersion;
        var revision = Management!.Revision;
        return RunOperationAsync(async token =>
        {
            _lifetime.TryApply(() => LastProbe = null);
            var result = await mcp.TestConnectivityAsync(entry.Name, token);
            token.ThrowIfCancellationRequested();
            if (IsRetired || _selectionVersion != selectionVersion || !SameKey(SelectedEntry?.Key, entry.Key)
                || Management?.Revision != revision || !OwnsEntry(entry)) return null;
            var error = result.WithoutData();
            var connectivity = result.Data;
            if (result.IsFailure() || connectivity is null)
            {
                var diagnostic = Redact(error.Message ?? GenericError, entry.McpExternalProfile?.Headers);
                _lifetime.TryApply(() => LastProbe = new(entry.Key, entry.Name, revision, null, diagnostic));
                return diagnostic;
            }
            var sanitized = connectivity with { Message = Redact(connectivity.Message, entry.McpExternalProfile?.Headers) };
            var probeError = sanitized.Success ? null : sanitized.Message;
            _lifetime.TryApply(() => LastProbe = new(entry.Key, entry.Name, revision, sanitized, probeError));
            return probeError;
        });
    }

    private Task<string?> ApplyManagementAsync(
        Func<CancellationToken, Task<Result<AgentCapabilityManagementInfo>>> producer,
        CancellationToken ct = default, IReadOnlyDictionary<string, string>? headers = null, string? selectName = null) =>
        RunOperationAsync(async token =>
        {
            var result = await producer(token);
            token.ThrowIfCancellationRequested();
            var error = result.WithoutData();
            var management = result.Data;
            if (result.IsFailure()) return Redact(error.Message ?? GenericError, headers);
            _lifetime.TryApply(() =>
            {
                Management = management;
                HasLoaded = true;
                LastProbe = null;
                EnsureSelectedEntry();
                if (selectName is not null && ActiveKind == AgentCapabilityKind.Mcp)
                {
                    var saved = CurrentEntries.FirstOrDefault(entry => SameKey(entry.Name, selectName));
                    if (saved is not null) _selectedKey = saved.Key;
                }
            });
            return _lifetime.IsRetired ? GenericError : null;
        }, ct);

    private async Task<string?> RunOperationAsync(Func<CancellationToken, Task<string?>> work, CancellationToken ct = default)
    {
        var admitted = false;
        _lifetime.TryApply(() =>
        {
            if (IsBusy) return;
            IsBusy = true;
            Error = null;
            admitted = true;
        });
        if (!admitted) return GenericError;
        try
        {
            return await _lifetime.RunAsync(async lifetimeToken =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, ct);
                var diagnostic = await work(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _lifetime.TryApply(() => Error = diagnostic);
                return diagnostic;
            });
        }
        catch (OperationCanceledException) when (_lifetime.Token.IsCancellationRequested || ct.IsCancellationRequested) { return GenericError; }
        catch (Exception) { _lifetime.TryApply(() => Error = GenericError); return GenericError; }
        finally { _lifetime.TryApply(() => IsBusy = false); }
    }

    private void EnsureSelectedEntry()
    {
        if (SelectedEntry is not null) return;
        _selectedKey = CurrentEntries.FirstOrDefault()?.Key;
        _selectionVersion++;
    }

    private bool OwnsEntry(AgentCapabilityEntryInfo entry) => !IsRetired
        && Management?.Entries.Any(item => ReferenceEquals(item, entry)) == true;
    private bool Matches(string value) => string.IsNullOrWhiteSpace(Query)
        || value.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase);
    private string GenericError => localizer["Error:Generic"].Value;
    private static bool SameKey(string? left, string? right) => left is not null && right is not null
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Redact(string diagnostic, IReadOnlyDictionary<string, string>? headers)
    {
        foreach (var value in (headers?.Values ?? []).Where(static value => !string.IsNullOrEmpty(value)).OrderByDescending(static value => value.Length))
            diagnostic = diagnostic.Replace(value, "[redacted]", StringComparison.Ordinal);
        return diagnostic;
    }

    private static bool SameProfile(ExternalMcpClientProfile left, ExternalMcpClientProfile right) =>
        SameKey(left.Name, right.Name) && left.Description == right.Description && left.Endpoint == right.Endpoint
        && left.TransportMode == right.TransportMode && left.ConnectionTimeoutSeconds == right.ConnectionTimeoutSeconds
        && left.IsAgentToolEnabled == right.IsAgentToolEnabled && left.Origin == right.Origin
        && left.Headers.Count == right.Headers.Count && left.Headers.All(header =>
            right.Headers.TryGetValue(header.Key, out var value) && value == header.Value);

    public ValueTask DisposeAsync() => _lifetime.RetireAsync();
}

/// <summary>Profile draft origin used for comparison after an editor awaits user input.</summary>
internal sealed record ExternalMcpProfileEditCapture(object Owner, string? Name, ExternalMcpClientProfile? Profile);

/// <summary>Actual probe evidence; a failed result envelope has no fabricated tool count or test timing.</summary>
internal sealed record CapabilityProbeOutcome(string EntryKey, string EntryName, long Revision, McpConnectivityTestResult? Result, string? Error);
