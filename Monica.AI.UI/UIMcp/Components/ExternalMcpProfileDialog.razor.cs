using Microsoft.AspNetCore.Components;
using ModelContextProtocol.Client;
using Monica.AI.AgentCapabilities.Models;
using Monica.AI.Mcp.Models;
using Monica.AI.UI.Shared.Support;
using Monica.Core.Results;
using MudBlazor;

namespace Monica.AI.UI.UIMcp.Components;

/// <summary>Owns an external MCP profile draft and connection evidence until save or cancellation.</summary>
public partial class ExternalMcpProfileDialog
{
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;
    /// <summary>Initial profile to edit; null starts an empty draft.</summary>
    [Parameter] public ExternalMcpClientProfile? Profile { get; set; }
    /// <summary>Locks the stable name when editing an existing profile.</summary>
    [Parameter] public bool IsEdit { get; set; }
    /// <summary>Optional localized save action label.</summary>
    [Parameter] public string? SubmitText { get; set; }
    /// <summary>
    /// Optional persistence callback. A null diagnostic closes with the normalized profile; an error retains the draft.
    /// Without a callback, the dialog returns the draft for the caller to persist. Cancellation cannot undo a completed write.
    /// </summary>
    [Parameter] public Func<ExternalMcpClientProfile, CancellationToken, Task<string?>>? OnSave { get; set; }

    private readonly AIAdministrationOperationLifetime _lifetime = new();
    private readonly List<HeaderRow> _headers = [];
    private McpConnectivityTestResult? _lastTestResult;
    private ExternalMcpClientProfile? _testedProfile;
    private bool _initialized, _isTesting, _isSaving, _errorIsTest;
    private string? _error;
    private string Name { get; set; } = string.Empty;
    private string Description { get; set; } = string.Empty;
    private string Endpoint { get; set; } = string.Empty;
    private HttpTransportMode TransportMode { get; set; } = HttpTransportMode.AutoDetect;
    private int ConnectionTimeoutSeconds { get; set; } = 30;
    private bool IsAgentToolEnabled { get; set; } = true;

    private string? EndpointValidationMessage => string.IsNullOrWhiteSpace(Endpoint)
        || Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        ? null : L["Capabilities:Mcp:Dialog:EndpointInvalid"].Value;

    private bool CanSubmit => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Description)
        && !string.IsNullOrWhiteSpace(Endpoint) && EndpointValidationMessage is null
        && ConnectionTimeoutSeconds is >= 1 and <= 300
        && _headers.All(static header => !string.IsNullOrWhiteSpace(header.Name));

    private bool TestResultMatches => _testedProfile is { } tested && ProfilesMatch(tested, CreateProfile());

    protected override void OnParametersSet()
    {
        if (_initialized) return;
        if (Profile is not null)
        {
            Name = Profile.Name; Description = Profile.Description; Endpoint = Profile.Endpoint;
            TransportMode = Profile.TransportMode; ConnectionTimeoutSeconds = Profile.ConnectionTimeoutSeconds;
            IsAgentToolEnabled = Profile.IsAgentToolEnabled;
            _headers.AddRange(Profile.Headers.Select(static header => new HeaderRow(header.Key, header.Value)));
        }
        _initialized = true;
    }

    private void OnNameChanged(string value) => Name = value.Trim();
    private void AddHeader() { if (!_isSaving && !_lifetime.IsRetired) _headers.Add(new HeaderRow()); }
    private void RemoveHeader(HeaderRow header)
    {
        if (_isSaving || _lifetime.IsRetired) return;
        _headers.Remove(header);
        header.Value = string.Empty;
    }

    private Task TestAsync() => _lifetime.RunAsync(async cancellationToken =>
    {
        if (_isTesting || _isSaving || !CanSubmit || _lifetime.IsRetired) return;
        var profile = CreateProfile();
        _isTesting = true; _lastTestResult = null; _testedProfile = null; _error = null; _errorIsTest = true;
        try
        {
            var result = await McpFacade.TestExternalProfileAsync(profile, cancellationToken);
            if (_lifetime.IsRetired || cancellationToken.IsCancellationRequested) return;
            _testedProfile = profile;
            if (!ProfilesMatch(profile, CreateProfile())) return;
            var error = result.WithoutData();
            var connectivity = result.Data;
            if (result.IsFailure()) _error = SafeDiagnostic(error.Message, profile);
            else { _lastTestResult = connectivity; _testedProfile = profile; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { if (!_lifetime.IsRetired) { _testedProfile = profile; _error = L["Error:Generic"]; } }
        finally { if (!_lifetime.IsRetired) _isTesting = false; }
    });

    private Task SubmitAsync() => _lifetime.RunAsync(async cancellationToken =>
    {
        if (_isTesting || _isSaving || !CanSubmit || _lifetime.IsRetired) return;
        var profile = CreateProfile();
        _isSaving = true; _error = null; _errorIsTest = false;
        try
        {
            var error = OnSave is { } save ? await save(profile, cancellationToken) : null;
            if (_lifetime.IsRetired || cancellationToken.IsCancellationRequested) return;
            if (error is not null) { _error = SafeDiagnostic(error, profile); return; }
            MudDialog.Close(DialogResult.Ok(profile));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { if (!_lifetime.IsRetired) _error = L["Error:Generic"]; }
        finally { if (!_lifetime.IsRetired) _isSaving = false; }
    });

    private ExternalMcpClientProfile CreateProfile() => new ExternalMcpClientProfile
    {
        Name = Name, Description = Description, Endpoint = Endpoint, TransportMode = TransportMode,
        ConnectionTimeoutSeconds = ConnectionTimeoutSeconds, IsAgentToolEnabled = IsAgentToolEnabled,
        Headers = _headers.Where(static header => !string.IsNullOrWhiteSpace(header.Name))
            .GroupBy(static header => header.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last().Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase),
        Origin = ExternalMcpClientProfileOrigin.User
    }.Normalize(ExternalMcpClientProfileOrigin.User);

    private static bool ProfilesMatch(ExternalMcpClientProfile first, ExternalMcpClientProfile second) =>
        first.Name == second.Name && first.Description == second.Description && first.Endpoint == second.Endpoint
        && first.TransportMode == second.TransportMode && first.ConnectionTimeoutSeconds == second.ConnectionTimeoutSeconds
        && first.IsAgentToolEnabled == second.IsAgentToolEnabled && first.Origin == second.Origin
        && first.Headers.Count == second.Headers.Count
        && first.Headers.All(header => second.Headers.TryGetValue(header.Key, out var value) && value == header.Value);

    private string SafeDiagnostic(string? message, ExternalMcpClientProfile profile)
    {
        var diagnostic = string.IsNullOrWhiteSpace(message) ? L["Error:Generic"].Value : message;
        foreach (var value in profile.Headers.Values.Where(static value => !string.IsNullOrWhiteSpace(value)).OrderByDescending(static value => value.Length))
            diagnostic = diagnostic.Replace(value, "[redacted]", StringComparison.Ordinal);
        return diagnostic;
    }

    private async Task Cancel()
    {
        var retirement = DisposeAsync();
        MudDialog.Cancel();
        await retirement;
    }

    private string FormatConnectivityMessage(McpConnectivityTestResult result) => result.Message switch
    {
        McpCapabilityMessageCode.Connectivity.ConnectedToEndpoint => L["Capabilities:Mcp:Connectivity:ConnectedToEndpoint", result.ToolCount],
        McpCapabilityMessageCode.Connectivity.ConnectedAndListedTools => L["Capabilities:Mcp:Connectivity:ConnectedAndListedTools", result.ToolCount],
        McpCapabilityMessageCode.Connectivity.LocalHttpDisplayUrlMissing => L["Capabilities:Mcp:Connectivity:LocalHttpDisplayUrlMissing", result.ToolCount],
        McpCapabilityMessageCode.Connectivity.LocalStdioInProcessReady => L["Capabilities:Mcp:Connectivity:LocalStdioInProcessReady", result.ToolCount],
        _ => _testedProfile is { } profile ? SafeDiagnostic(result.Message, profile) : L["Error:Generic"]
    };

    /// <summary>Cancels and drains admitted tests and saves, then clears dialog-held header values.</summary>
    public ValueTask DisposeAsync() => _lifetime.RetireAsync(() =>
    {
        foreach (var header in _headers) header.Value = string.Empty;
        _headers.Clear();
        _testedProfile?.Headers.Clear();
        _testedProfile = null; _lastTestResult = null; _error = null;
        return ValueTask.CompletedTask;
    });

    private sealed class HeaderRow(string name = "", string value = "")
    {
        public string Name { get; set; } = name;
        public string Value { get; set; } = value;
    }
}
