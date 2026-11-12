using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.SignalR.Facades;
using Monica.SignalR.Models;
using Monica.SignalR.UISignalR.Models;
using Monica.SignalR.UISignalR.Support;
using Monica.Modules;

namespace Monica.SignalR.UISignalR.State;

/// <summary>
/// Holds all mutable state for the SignalR debug page.
/// </summary>
public sealed class SignalRDebugPageState(
    IOptions<ModuleSignalRUIOption> options,
    SignalRFacade signalRFacade,
    SignalRDebugJsClient jsClient,
    SignalRDebugTestTokenService testTokenService)
    : IAsyncDisposable
{
    private bool _disposed;
    private bool _verboseLogging;
    private bool _connectedUsersRefreshInProgress;
    private bool _connectedUsersRefreshRequested;
    private string _selectedHubRoute = string.Empty;
    private Task? _initializationTask;
    private Task? _connectedUsersRefreshTask;
    private Task? _disposeTask;

    /// <summary>
    /// Raised whenever the page should re-render.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Gets or sets the access token used when connecting from the browser.
    /// </summary>
    public string AccessToken { get; set; } = options.Value.DefaultAccessToken ?? string.Empty;

    /// <summary>
    /// Gets or sets the draft target for the next browser connection. Editing it never retargets a live connection.
    /// </summary>
    public string SelectedHubRoute
    {
        get => _selectedHubRoute;
        set => SelectHubRoute(value);
    }

    /// <summary>
    /// Gets the mapped route targeted by the established or pending browser connection, or an empty value when disconnected.
    /// </summary>
    public string ConnectedHubRoute { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the route represented by the method catalog: the active connection target, or the draft target when disconnected.
    /// </summary>
    public string MethodCatalogHubRoute => jsClient.MethodCatalogHubRoute;

    /// <summary>
    /// Gets or sets the user name used by the quick send box.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the quick message content.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets the currently selected hub method name.
    /// </summary>
    public string SelectedMethodName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether verbose conversion logs are enabled.
    /// </summary>
    public bool VerboseLogging
    {
        get => _verboseLogging;
        set
        {
            if (_verboseLogging == value)
            {
                return;
            }

            _verboseLogging = value;
            jsClient.IsVerboseLoggingEnabled = value;
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Gets the available hubs.
    /// </summary>
    public List<SignalRHubInfo> AvailableHubs { get; } = [];

    /// <summary>Gets whether hub metadata has been captured successfully, including an empty capture.</summary>
    public bool HubsLoaded { get; private set; }

    /// <summary>Gets whether a hub metadata capture is in progress.</summary>
    public bool IsLoadingHubs { get; private set; }

    /// <summary>Gets whether the latest hub metadata capture failed; previous evidence remains available.</summary>
    public bool HubsLoadFailed { get; private set; }

    /// <summary>Gets the UTC time of the last successful hub metadata capture, or null before the first success.</summary>
    public DateTime? HubsCapturedAtUtc { get; private set; }

    /// <summary>
    /// Gets the methods for the active connection target, or the selected draft hub while disconnected.
    /// </summary>
    public List<HubMethodInfo> HubMethods { get; } = [];

    /// <summary>
    /// Gets the current method parameter values.
    /// </summary>
    public Dictionary<string, string> MethodParameters { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the message log.
    /// </summary>
    public List<SignalRMessage> Messages { get; } = [];

    /// <summary>
    /// Gets the connected user snapshot.
    /// </summary>
    public List<SignalRConnectedUserInfo> ConnectedUsers { get; } = [];

    /// <summary>Gets whether tracked connections have been captured successfully, including an empty capture.</summary>
    public bool ConnectedUsersLoaded { get; private set; }

    /// <summary>Gets whether a tracked-connection capture is in progress.</summary>
    public bool IsLoadingConnectedUsers { get; private set; }

    /// <summary>Gets whether the latest tracked-connection capture failed; previous evidence remains available.</summary>
    public bool ConnectedUsersLoadFailed { get; private set; }

    /// <summary>Gets the UTC time of the last successful tracked-connection capture, or null before the first success.</summary>
    public DateTime? ConnectedUsersCapturedAtUtc { get; private set; }

    /// <summary>
    /// Gets the latest server-side send diagnostics snapshot.
    /// </summary>
    public SignalRSendDiagnosticsSnapshot? SendDiagnostics { get; private set; }

    /// <summary>Gets whether a server send diagnostics capture is in progress.</summary>
    public bool IsLoadingSendDiagnostics { get; private set; }

    /// <summary>Gets whether the latest send diagnostics capture failed; the previous snapshot remains available.</summary>
    public bool SendDiagnosticsLoadFailed { get; private set; }

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    public SignalRConnectionState ConnectionState { get; private set; } = new();

    /// <summary>
    /// Gets a value indicating whether the host allows minting a random test-user token for local debugging.
    /// </summary>
    public bool CanGenerateTestToken => testTokenService.IsEnabled;

    /// <summary>
    /// Gets the username of the most recently generated test-user token, if any.
    /// </summary>
    public string? LastTestUserName { get; private set; }

    /// <summary>
    /// Gets whether a connected browser was absent from the latest successful host registry capture.
    /// </summary>
    /// <remarks>
    /// Registry absence is not an authentication result. Anonymous connections, hubs that do not use Monica's tracking
    /// base class, and connections observed by another host can all be absent from this host's registry.
    /// </remarks>
    public bool IsConnectionNotObserved { get; private set; }

    /// <summary>
    /// Gets the legacy registry-absence diagnostic. This does not establish that the connection is anonymous.
    /// </summary>
    public bool IsServerSideAnonymous => IsConnectionNotObserved;

    /// <summary>
    /// Initializes the page state and JavaScript client.
    /// </summary>
    public Task InitializeAsync() => _disposed
        ? Task.CompletedTask
        : _initializationTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        jsClient.MessageReceived += OnMessageReceived;
        jsClient.ConnectionStateChanged += OnConnectionStateChanged;
        jsClient.MethodListenerChanged += OnMethodListenerChanged;

        await jsClient.InitializeAsync();
        if (_disposed)
        {
            return;
        }
        jsClient.IsVerboseLoggingEnabled = _verboseLogging;

        await LoadHubsAsync();
        await LoadConnectedUsersAsync();
        await LoadSendDiagnosticsAsync();

        SyncFromJsClient();
        NotifyStateChanged();
    }

    /// <summary>
    /// Loads hub metadata from the SignalR facade.
    /// </summary>
    public async Task<bool> LoadHubsAsync()
    {
        if (_disposed || IsLoadingHubs)
        {
            return false;
        }

        IsLoadingHubs = true;
        HubsLoadFailed = false;
        NotifyStateChanged();
        try
        {
            var result = await signalRFacade.GetHubInfosAsync();
            if (_disposed)
            {
                return false;
            }
            var hubInfos = result.Data;
            if (result.IsFailure() || hubInfos is null)
            {
                HubsLoadFailed = true;
                return false;
            }

            AvailableHubs.Clear();
            AvailableHubs.AddRange(hubInfos);
            HubsLoaded = true;
            HubsCapturedAtUtc = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(SelectedHubRoute) && AvailableHubs.Count > 0)
            {
                _selectedHubRoute = AvailableHubs[0].Route;
            }

            UpdateMethodCatalog(force: true);
            SyncFromJsClient();
            return true;
        }
        finally
        {
            IsLoadingHubs = false;
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Loads the currently connected users from the SignalR facade.
    /// </summary>
    public async Task<bool> LoadConnectedUsersAsync()
    {
        if (_disposed || IsLoadingConnectedUsers)
        {
            return false;
        }

        IsLoadingConnectedUsers = true;
        ConnectedUsersLoadFailed = false;
        IsConnectionNotObserved = false;
        NotifyStateChanged();
        try
        {
            var result = await signalRFacade.GetConnectedUsersAsync();
            if (_disposed)
            {
                return false;
            }
            var users = result.Data;
            if (result.IsFailure() || users is null)
            {
                ConnectedUsersLoadFailed = true;
                return false;
            }

            ConnectedUsers.Clear();
            ConnectedUsers.AddRange(users);
            ConnectedUsersLoaded = true;
            ConnectedUsersCapturedAtUtc = DateTime.UtcNow;
            return true;
        }
        finally
        {
            IsLoadingConnectedUsers = false;
            UpdateConnectionRegistryDiagnostic();
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Loads the current server-side send diagnostics snapshot from the SignalR facade.
    /// </summary>
    public async Task<bool> LoadSendDiagnosticsAsync()
    {
        if (_disposed || IsLoadingSendDiagnostics)
        {
            return false;
        }

        IsLoadingSendDiagnostics = true;
        SendDiagnosticsLoadFailed = false;
        NotifyStateChanged();
        try
        {
            var result = await signalRFacade.GetSendDiagnosticsAsync();
            if (_disposed)
            {
                return false;
            }
            var snapshot = result.Data;
            if (result.IsFailure() || snapshot is null)
            {
                SendDiagnosticsLoadFailed = true;
                return false;
            }

            SendDiagnostics = snapshot;
            return true;
        }
        finally
        {
            IsLoadingSendDiagnostics = false;
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Connects the browser debug client to the selected hub.
    /// </summary>
    public async Task<bool> ConnectAsync(string baseUri)
    {
        if (_disposed || ConnectionState.IsConnecting || ConnectionState.Status == "Disconnecting"
            || string.IsNullOrWhiteSpace(SelectedHubRoute))
        {
            return false;
        }

        ConnectedHubRoute = SelectedHubRoute;
        UpdateMethodCatalog(force: true);
        SyncMethodCatalog();
        var hubUrl = BuildHubUrl(baseUri, ConnectedHubRoute);
        var success = await jsClient.ConnectAsync(hubUrl, AccessToken);
        if (_disposed)
        {
            return false;
        }
        SyncFromJsClient();
        NotifyStateChanged();
        await RefreshConnectedUsersAfterConnectionChangeAsync();
        return success;
    }

    /// <summary>
    /// Mints a random test-user token, fills the token field with it, and connects to the selected hub.
    /// </summary>
    /// <param name="baseUri">The app base URI used to build the hub URL.</param>
    /// <returns><c>true</c> when the test user was minted and the connection succeeded.</returns>
    public async Task<bool> TestUserConnectAsync(string baseUri)
    {
        if (_disposed)
        {
            return false;
        }
        SignalRDebugTestToken token;
        try
        {
            token = testTokenService.GenerateTestUserToken();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        // Keep the minted token in the input so it stays visible and copyable while stepping through breakpoints.
        AccessToken = token.AccessToken;
        LastTestUserName = token.Username;
        NotifyStateChanged();

        return await ConnectAsync(baseUri);
    }

    /// <summary>
    /// Disconnects the browser debug client.
    /// </summary>
    public async Task<bool> DisconnectAsync()
    {
        var success = await jsClient.DisconnectAsync();
        if (_disposed)
        {
            return false;
        }
        SyncFromJsClient();
        IsConnectionNotObserved = false;
        NotifyStateChanged();
        await RefreshConnectedUsersAfterConnectionChangeAsync();
        return success;
    }

    /// <summary>
    /// Sends the quick message currently entered on the page.
    /// </summary>
    public async Task<bool> SendMessageAsync()
    {
        var success = await jsClient.SendMessageAsync(UserName, Message);
        SyncFromJsClient();
        NotifyStateChanged();
        return success;
    }

    /// <summary>
    /// Invokes the currently selected hub method.
    /// </summary>
    public async Task<bool> InvokeSelectedMethodAsync()
    {
        var selectedMethod = HubMethods.FirstOrDefault(method => method.Name == SelectedMethodName);
        if (selectedMethod is null)
        {
            return false;
        }

        var parameters = selectedMethod.Args
            .Select(argumentInfo => new MethodCallParameter
            {
                Name = argumentInfo.Name,
                Type = argumentInfo.TypeName,
                Value = MethodParameters.GetValueOrDefault(argumentInfo.Name, string.Empty)
            })
            .ToList();

        var success = await jsClient.InvokeMethodAsync(SelectedMethodName, parameters);
        SyncFromJsClient();
        NotifyStateChanged();
        return success;
    }

    /// <summary>
    /// Enables or disables a single method listener.
    /// </summary>
    public async Task<bool> ToggleMethodListenerAsync(HubMethodInfo method, bool isListening)
    {
        var success = await jsClient.ToggleMethodListenerAsync(method.Name, isListening);
        SyncFromJsClient();
        NotifyStateChanged();
        return success;
    }

    /// <summary>
    /// Enables all available method listeners.
    /// </summary>
    public async Task<int> EnableAllListenersAsync()
    {
        var successCount = await jsClient.EnableAllListenersAsync();
        SyncFromJsClient();
        NotifyStateChanged();
        return successCount;
    }

    /// <summary>
    /// Disables all active method listeners.
    /// </summary>
    public async Task<int> DisableAllListenersAsync()
    {
        var successCount = await jsClient.DisableAllListenersAsync();
        SyncFromJsClient();
        NotifyStateChanged();
        return successCount;
    }

    /// <summary>
    /// Clears the page message log.
    /// </summary>
    public void ClearMessages()
    {
        jsClient.ClearMessages();
        SyncFromJsClient();
        NotifyStateChanged();
    }

    /// <summary>
    /// Applies a newly selected hub method and normalizes the parameter bag.
    /// </summary>
    public void SelectMethod(string methodName)
    {
        if (_disposed)
        {
            return;
        }
        SelectedMethodName = methodName;

        var selectedMethod = HubMethods.FirstOrDefault(method => method.Name == methodName);
        if (selectedMethod is null)
        {
            MethodParameters.Clear();
            NotifyStateChanged();
            return;
        }

        foreach (var argumentInfo in selectedMethod.Args)
        {
            MethodParameters.TryAdd(argumentInfo.Name, string.Empty);
        }

        var activeArgumentNames = selectedMethod.Args
            .Select(argumentInfo => argumentInfo.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in MethodParameters.Keys.Where(key => !activeArgumentNames.Contains(key)).ToList())
        {
            MethodParameters.Remove(key);
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// Updates the next connection target and, while disconnected, its method catalog.
    /// </summary>
    /// <param name="hubRoute">The mapped route selected for the next connection.</param>
    public void SelectHubRoute(string hubRoute)
    {
        if (_disposed || string.Equals(_selectedHubRoute, hubRoute, StringComparison.Ordinal))
        {
            return;
        }

        _selectedHubRoute = hubRoute;
        UpdateMethodCatalog();
        SyncMethodCatalog();
        NotifyStateChanged();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _disposed = true;

        jsClient.MessageReceived -= OnMessageReceived;
        jsClient.ConnectionStateChanged -= OnConnectionStateChanged;
        jsClient.MethodListenerChanged -= OnMethodListenerChanged;

        try
        {
            await jsClient.DisposeAsync();
        }
        finally
        {
            if (_connectedUsersRefreshTask is not null)
            {
                await _connectedUsersRefreshTask;
            }
        }
    }

    private void OnMessageReceived(SignalRMessage _)
    {
        SyncFromJsClient();
        NotifyStateChanged();
    }

    private void OnConnectionStateChanged(SignalRConnectionState state)
    {
        SyncFromJsClient();
        NotifyStateChanged();
        _ = RefreshConnectedUsersAfterConnectionChangeAsync();
    }

    private void OnMethodListenerChanged(HubMethodInfo _)
    {
        SyncFromJsClient();
        NotifyStateChanged();
    }

    /// <summary>
    /// Refreshes the connected-user snapshot after the browser connection reached or left a stable state.
    /// </summary>
    /// <remarks>
    /// The server registers a connection in <c>OnConnectedAsync</c> before the browser reports the established
    /// connection, so refreshing here observes the debug client itself. Failures are swallowed because this is a
    /// best-effort auto refresh; the manual refresh button still surfaces errors.
    /// </remarks>
    private Task RefreshConnectedUsersAfterConnectionChangeAsync()
    {
        if (_disposed
            || ConnectionState.Status is not ("Connected" or "Disconnected"))
        {
            return Task.CompletedTask;
        }

        _connectedUsersRefreshRequested = true;
        return _connectedUsersRefreshInProgress
            ? _connectedUsersRefreshTask ?? Task.CompletedTask
            : _connectedUsersRefreshTask = RefreshConnectedUsersCoreAsync();
    }

    private async Task RefreshConnectedUsersCoreAsync()
    {
        _connectedUsersRefreshInProgress = true;
        try
        {
            do
            {
                _connectedUsersRefreshRequested = false;
                await LoadConnectedUsersAsync();
            }
            while (!_disposed && _connectedUsersRefreshRequested);
        }
        catch (Exception)
        {
            // Best-effort auto refresh; the manual refresh button reports failures to the user.
        }
        finally
        {
            _connectedUsersRefreshInProgress = false;
        }

        UpdateConnectionRegistryDiagnostic();
        NotifyStateChanged();
    }

    /// <summary>
    /// Reports registry absence only when the latest capture succeeded and the browser is connected.
    /// </summary>
    private void UpdateConnectionRegistryDiagnostic()
    {
        if (_disposed)
        {
            return;
        }

        IsConnectionNotObserved = ConnectedUsersLoaded
            && !IsLoadingConnectedUsers
            && !ConnectedUsersLoadFailed
            && ConnectionState.IsConnected
            && !string.IsNullOrEmpty(ConnectionState.ConnectionId)
            && ConnectedUsers.All(user =>
                !string.Equals(user.ConnectionId, ConnectionState.ConnectionId, StringComparison.Ordinal));
    }

    private void SyncFromJsClient()
    {
        if (_disposed)
        {
            return;
        }

        Messages.Clear();
        Messages.AddRange(jsClient.GetMessages());

        ConnectionState = jsClient.GetConnectionState();
        if (!HasActiveConnectionTarget)
        {
            ConnectedHubRoute = string.Empty;
            IsConnectionNotObserved = false;
        }

        UpdateMethodCatalog();
        SyncMethodCatalog();
    }

    private void SyncMethodCatalog()
    {
        HubMethods.Clear();
        HubMethods.AddRange(jsClient.GetHubMethods());
    }

    private bool HasActiveConnectionTarget => ConnectionState.IsConnecting
        || ConnectionState.Status is "Connected" or "Connecting" or "Reconnecting" or "Disconnecting";

    private void UpdateMethodCatalog(bool force = false)
    {
        var route = HasActiveConnectionTarget ? ConnectedHubRoute : SelectedHubRoute;
        var routeChanged = !string.Equals(route, MethodCatalogHubRoute, StringComparison.Ordinal);
        if (!force && !routeChanged)
        {
            return;
        }

        jsClient.SetHubMetadata(AvailableHubs, route);
        if (routeChanged || !jsClient.GetHubMethods().Any(method => method.Name == SelectedMethodName))
        {
            SelectedMethodName = string.Empty;
            MethodParameters.Clear();
        }
    }

    private void NotifyStateChanged()
    {
        if (!_disposed)
        {
            StateChanged?.Invoke();
        }
    }

    private static string BuildHubUrl(string baseUri, string selectedHubRoute)
    {
        var normalizedBaseUri = baseUri.TrimEnd('/');
        var normalizedHubRoute = selectedHubRoute.StartsWith("/", StringComparison.Ordinal)
            ? selectedHubRoute
            : $"/{selectedHubRoute}";

        return $"{normalizedBaseUri}{normalizedHubRoute}";
    }
}
