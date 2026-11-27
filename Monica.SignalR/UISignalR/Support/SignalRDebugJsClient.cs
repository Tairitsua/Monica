using System.Text.Json;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Monica.SignalR.Localization;
using Monica.SignalR.Models;
using Monica.SignalR.UISignalR.Models;

namespace Monica.SignalR.UISignalR.Support;

/// <summary>
/// Encapsulates browser-side SignalR debug interactions and JavaScript callbacks.
/// </summary>
public sealed class SignalRDebugJsClient(
    IJSRuntime jsRuntime,
    SignalRInvocationArgumentParser argumentParser,
    IStringLocalizer<SignalRResource> localizer)
    : IAsyncDisposable
{
    private const int MAX_MESSAGE_COUNT = 1000;
    private static readonly TimeSpan SHUTDOWN_DRAIN_TIMEOUT = TimeSpan.FromSeconds(5);

    private readonly List<SignalRMessage> _messages = [];
    private readonly List<HubMethodInfo> _hubMethods = [];
    private readonly Dictionary<string, Dictionary<string, HubMethodInfo>> _methodsByHubRoute = new(StringComparer.Ordinal);
    private readonly SignalRConnectionState _connectionState = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly object _disposeSync = new();
    private IJSObjectReference? _module;
    private IJSObjectReference? _session;
    private DotNetObjectReference<SignalRDebugJsClient>? _dotNetRef;
    private Task? _disposeTask;
    private volatile bool _disposed;
    private long _connectionGeneration;

    /// <summary>
    /// Gets the mapped hub route represented by the current method catalog.
    /// </summary>
    public string MethodCatalogHubRoute { get; private set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether verbose argument conversion logs are emitted.
    /// </summary>
    public bool IsVerboseLoggingEnabled { get; set; }

    /// <summary>
    /// Raised when a new log message is received.
    /// </summary>
    public event Action<SignalRMessage>? MessageReceived;

    /// <summary>
    /// Raised when the connection state changes.
    /// </summary>
    public event Action<SignalRConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Raised when a hub method listener changes state.
    /// </summary>
    public event Action<HubMethodInfo>? MethodListenerChanged;

    /// <summary>
    /// Initializes JavaScript callbacks.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_session is not null)
        {
            return;
        }

        await _sessionGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                return;
            }

            var module = await jsRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                "./_content/Monica.SignalR/UISignalR/signalr-debug.js");
            if (_disposed)
            {
                await DisposeInteropReferenceAsync(module);
                return;
            }

            var dotNetRef = DotNetObjectReference.Create(this);
            IJSObjectReference session;

            try
            {
                session = await module.InvokeAsync<IJSObjectReference>(
                    "createSession",
                    dotNetRef,
                    BuildJavaScriptLocalization());
            }
            catch
            {
                dotNetRef.Dispose();
                await DisposeInteropReferenceAsync(module);
                throw;
            }

            if (_disposed)
            {
                await DisposeOwnedSessionAsync(session, dotNetRef, module);
                return;
            }

            _module = module;
            _dotNetRef = dotNetRef;
            _session = session;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// Updates the method catalog for one mapped hub while retaining that route's listener state and received counts.
    /// </summary>
    /// <param name="hubInfos">The latest mapped hub metadata.</param>
    /// <param name="hubRoute">The route to inspect, or the current catalog route and then the first route when omitted.</param>
    public void SetHubMetadata(IReadOnlyList<SignalRHubInfo> hubInfos, string? hubRoute = null)
    {
        if (_disposed)
        {
            return;
        }

        var route = hubRoute ?? (string.IsNullOrEmpty(MethodCatalogHubRoute)
            ? hubInfos.FirstOrDefault()?.Route ?? string.Empty
            : MethodCatalogHubRoute);
        MethodCatalogHubRoute = route;
        _hubMethods.Clear();
        var hubInfo = hubInfos.FirstOrDefault(hub => string.Equals(hub.Route, route, StringComparison.Ordinal));
        if (hubInfo is null)
        {
            return;
        }

        if (!_methodsByHubRoute.TryGetValue(route, out var methods))
        {
            methods = new Dictionary<string, HubMethodInfo>(StringComparer.Ordinal);
            _methodsByHubRoute.Add(route, methods);
        }

        foreach (var methodInfo in hubInfo.Methods)
        {
            if (!methods.TryGetValue(methodInfo.Name, out var method))
            {
                method = new HubMethodInfo { Name = methodInfo.Name };
                methods.Add(methodInfo.Name, method);
            }

            method.DisplayName = $"{methodInfo.Description} ({methodInfo.Name})";
            method.Args = methodInfo.Parameters
                .Select(parameterInfo => new SignalRHubParameterInfo
                {
                    Name = parameterInfo.Name,
                    TypeName = parameterInfo.TypeName
                })
                .ToList();
            _hubMethods.Add(method);
        }
    }

    /// <summary>
    /// Connects to the selected SignalR hub.
    /// </summary>
    public async Task<bool> ConnectAsync(string hubUrl, string accessToken)
    {
        if (_disposed || _connectionState.IsConnecting || _connectionState.Status == "Disconnecting")
        {
            return false;
        }

        var connectionGeneration = Interlocked.Increment(ref _connectionGeneration);
        try
        {
            _connectionState.IsConnecting = true;
            _connectionState.Status = "Connecting";
            _connectionState.ConnectionId = string.Empty;
            ResetMethodListeners();
            RaiseConnectionStateChanged();

            var result = await InvokeSessionAsync("connect", hubUrl, accessToken, connectionGeneration);
            if (result is null || connectionGeneration != Volatile.Read(ref _connectionGeneration))
            {
                return false;
            }

            if (result.Value.GetProperty("success").GetBoolean())
            {
                if (_connectionState.Status == "Connecting")
                {
                    _connectionState.Status = "Connected";
                }
                if (_connectionState.IsConnected && string.IsNullOrEmpty(_connectionState.ConnectionId)
                    && result.Value.TryGetProperty("connectionId", out var connectionId))
                {
                    _connectionState.ConnectionId = connectionId.GetString() ?? string.Empty;
                }
                AddMessage("System", T("UISignalR:DebugClient:ConnectedToHub", hubUrl), MessageType.Success);
                return true;
            }

            AddMessage("System", T("UISignalR:DebugClient:ConnectionFailed", result.Value.GetProperty("error").GetString() ?? string.Empty), MessageType.Error, ReadErrorDetails(result.Value));
            return false;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            if (!_disposed)
            {
                AddMessage("System", T("UISignalR:DebugClient:ConnectionFailed", ex.Message), MessageType.Error, ex.ToString());
            }

            return false;
        }
        finally
        {
            if (connectionGeneration == Volatile.Read(ref _connectionGeneration))
            {
                _connectionState.IsConnecting = false;
                if (_connectionState.Status == "Connecting")
                {
                    _connectionState.Status = "Disconnected";
                    _connectionState.ConnectionId = string.Empty;
                }
                if (!_disposed)
                {
                    RaiseConnectionStateChanged();
                }
            }
        }
    }

    /// <summary>
    /// Disconnects the current browser-side SignalR connection.
    /// </summary>
    public async Task<bool> DisconnectAsync()
    {
        if (_disposed || _connectionState.Status == "Disconnecting")
        {
            return false;
        }

        var connectionGeneration = Interlocked.Increment(ref _connectionGeneration);
        _connectionState.IsConnecting = false;
        _connectionState.Status = "Disconnecting";
        RaiseConnectionStateChanged();
        try
        {
            var result = await InvokeSessionAsync("disconnect", connectionGeneration);
            if (result is null)
            {
                return false;
            }

            if (result.Value.GetProperty("success").GetBoolean())
            {
                AddMessage("System", T("UISignalR:DebugClient:Disconnected"), MessageType.Info);
                return true;
            }

            AddMessage("System", T("UISignalR:DebugClient:DisconnectFailed", result.Value.GetProperty("error").GetString() ?? string.Empty), MessageType.Error, ReadErrorDetails(result.Value));
            return false;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            if (!_disposed)
            {
                AddMessage("System", T("UISignalR:DebugClient:DisconnectFailed", ex.Message), MessageType.Error, ex.ToString());
            }

            return false;
        }
        finally
        {
            // The browser detaches its connection and listeners before awaiting transport shutdown.
            _connectionState.Status = "Disconnected";
            _connectionState.ConnectionId = string.Empty;
            ResetMethodListeners();
            if (!_disposed)
            {
                RaiseConnectionStateChanged();
            }
        }
    }

    /// <summary>
    /// Sends a quick message through the JavaScript debug client.
    /// </summary>
    public async Task<bool> SendMessageAsync(string userName, string message)
    {
        try
        {
            var result = await InvokeSessionAsync("sendMessage", userName, message);
            if (result is null)
            {
                return false;
            }

            if (result.Value.GetProperty("success").GetBoolean())
            {
                return true;
            }

            AddMessage("Error", T("UISignalR:DebugClient:SendFailed", result.Value.GetProperty("error").GetString() ?? string.Empty), MessageType.Error, ReadErrorDetails(result.Value));
            return false;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            if (!_disposed)
            {
                AddMessage("Error", T("UISignalR:DebugClient:SendFailed", ex.Message), MessageType.Error, ex.ToString());
            }

            return false;
        }
    }

    /// <summary>
    /// Invokes the selected hub method with typed arguments.
    /// </summary>
    public async Task<bool> InvokeMethodAsync(string methodName, IReadOnlyList<MethodCallParameter> parameters)
    {
        if (_disposed)
        {
            return false;
        }

        var method = _hubMethods.FirstOrDefault(candidate => candidate.Name == methodName);
        if (method is null)
        {
            AddMessage("Error", T("UISignalR:DebugClient:MethodNotFound", methodName), MessageType.Error);
            return false;
        }

        try
        {
            var arguments = new List<object?>(method.Args.Count);

            if (IsVerboseLoggingEnabled)
            {
                AddMessage("System", T("UISignalR:DebugClient:InvokingMethod", methodName), MessageType.Info);
            }

            foreach (var argumentInfo in method.Args)
            {
                var parameter = parameters.FirstOrDefault(candidate => candidate.Name == argumentInfo.Name);
                var rawValue = parameter?.Value ?? string.Empty;

                if (IsVerboseLoggingEnabled)
                {
                    AddMessage("System", T("UISignalR:DebugClient:ArgumentValue", argumentInfo.Name, argumentInfo.TypeName, rawValue), MessageType.Info);
                }

                var convertedValue = argumentParser.ConvertValue(rawValue, argumentInfo.TypeName);
                arguments.Add(convertedValue);

                if (IsVerboseLoggingEnabled)
                {
                    AddMessage(
                        "System",
                        T(
                            "UISignalR:DebugClient:ArgumentConverted",
                            argumentInfo.Name,
                            convertedValue?.GetType().Name ?? "null",
                            convertedValue?.ToString() ?? "null"),
                        MessageType.Info);
                }
            }

            var result = await InvokeSessionAsync(
                "invokeMethod",
                methodName,
                arguments.ToArray());
            if (result is null)
            {
                return false;
            }

            if (result.Value.GetProperty("success").GetBoolean())
            {
                return true;
            }

            AddMessage("Error", T("UISignalR:DebugClient:MethodInvocationFailed", result.Value.GetProperty("error").GetString() ?? string.Empty), MessageType.Error, ReadErrorDetails(result.Value));
            return false;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            if (!_disposed)
            {
                AddMessage("Error", T("UISignalR:DebugClient:MethodInvocationFailed", ex.Message), MessageType.Error, ex.ToString());
            }

            return false;
        }
    }

    /// <summary>
    /// Enables or disables a method listener.
    /// </summary>
    public async Task<bool> ToggleMethodListenerAsync(string methodName, bool isListening)
    {
        if (_disposed || !_connectionState.IsConnected)
        {
            return false;
        }

        var method = _hubMethods.FirstOrDefault(candidate => candidate.Name == methodName);
        if (method is null)
        {
            return false;
        }

        var connectionGeneration = Volatile.Read(ref _connectionGeneration);
        var hubRoute = MethodCatalogHubRoute;
        try
        {
            var result = await InvokeSessionAsync(
                isListening ? "registerListener" : "unregisterListener",
                method.Name,
                method.DisplayName,
                hubRoute);
            if (result is null || connectionGeneration != Volatile.Read(ref _connectionGeneration)
                || !string.Equals(hubRoute, MethodCatalogHubRoute, StringComparison.Ordinal))
            {
                return false;
            }

            if (!result.Value.GetProperty("success").GetBoolean())
            {
                AddMessage(
                    "Error",
                    isListening
                        ? T("UISignalR:DebugClient:RegisterListenerFailed", result.Value.GetProperty("error").GetString() ?? string.Empty)
                        : T("UISignalR:DebugClient:UnregisterListenerFailed", result.Value.GetProperty("error").GetString() ?? string.Empty),
                    MessageType.Error);
                return false;
            }

            method.IsListening = isListening;
            MethodListenerChanged?.Invoke(method);
            return true;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            if (!_disposed)
            {
                AddMessage("Error", T("UISignalR:DebugClient:ListenerToggleFailed", ex.Message), MessageType.Error, ex.ToString());
            }

            return false;
        }
    }

    /// <summary>
    /// Enables all available method listeners.
    /// </summary>
    public async Task<int> EnableAllListenersAsync()
    {
        var successCount = 0;

        foreach (var method in _hubMethods.Where(method => !method.IsListening).ToList())
        {
            if (await ToggleMethodListenerAsync(method.Name, true))
            {
                successCount++;
            }
        }

        return successCount;
    }

    /// <summary>
    /// Disables all active method listeners.
    /// </summary>
    public async Task<int> DisableAllListenersAsync()
    {
        var successCount = 0;

        foreach (var method in _hubMethods.Where(method => method.IsListening).ToList())
        {
            if (await ToggleMethodListenerAsync(method.Name, false))
            {
                successCount++;
            }
        }

        return successCount;
    }

    /// <summary>
    /// Clears the in-memory message log.
    /// </summary>
    public void ClearMessages()
    {
        if (_disposed)
        {
            return;
        }

        _messages.Clear();
        _connectionState.TotalReceivedMessages = 0;
        RaiseConnectionStateChanged();
    }

    /// <summary>
    /// Returns a snapshot of the current message log.
    /// </summary>
    public IReadOnlyList<SignalRMessage> GetMessages() => _messages.AsReadOnly();

    /// <summary>
    /// Returns a snapshot of the current hub method catalog.
    /// </summary>
    public IReadOnlyList<HubMethodInfo> GetHubMethods() => _hubMethods.AsReadOnly();

    /// <summary>
    /// Returns a snapshot of the current connection state.
    /// </summary>
    public SignalRConnectionState GetConnectionState()
    {
        return new SignalRConnectionState
        {
            Status = _connectionState.Status,
            ConnectionId = _connectionState.ConnectionId,
            IsConnecting = _connectionState.IsConnecting,
            TotalReceivedMessages = _connectionState.TotalReceivedMessages
        };
    }

    /// <summary>
    /// Receives log messages from JavaScript callbacks.
    /// </summary>
    [JSInvokable("Invoke")]
    public void Invoke(string source, string content, string type)
    {
        if (_disposed)
        {
            return;
        }

        var messageType = type switch
        {
            "Sent" => MessageType.Sent,
            "Received" => MessageType.Received,
            "System" => MessageType.System,
            "Success" => MessageType.Success,
            "Error" => MessageType.Error,
            _ => MessageType.Info
        };

        if (messageType == MessageType.Received)
        {
            _connectionState.TotalReceivedMessages++;

            var methodDisplayName = content.Split(':', 2)[0];
            var method = _hubMethods.FirstOrDefault(candidate =>
                string.Equals(candidate.DisplayName, methodDisplayName, StringComparison.Ordinal));

            if (method is not null)
            {
                method.ReceivedCount++;
                MethodListenerChanged?.Invoke(method);
            }
        }

        AddMessage(source, content, messageType, messageType == MessageType.Sent && content.IndexOf(':') is var separator && separator >= 0 ? content[(separator + 1)..].Trim() : null);
    }

    /// <summary>
    /// Receives a listener callback identified by its exact method name rather than its mutable display text.
    /// </summary>
    /// <param name="methodName">The client callback method name registered in the browser.</param>
    /// <param name="argumentDisplay">The serialized arguments, or the localized no-arguments text.</param>
    /// <param name="hubRoute">The originating mapped route, used to reject callbacks from a replaced connection.</param>
    /// <param name="connectionGeneration">The connection generation that produced the callback.</param>
    [JSInvokable("OnMethodReceived")]
    public void OnMethodReceived(string methodName, string argumentDisplay, string hubRoute, long connectionGeneration)
    {
        if (_disposed || _connectionState.Status != "Connected"
            || connectionGeneration != Volatile.Read(ref _connectionGeneration)
            || !string.Equals(hubRoute, MethodCatalogHubRoute, StringComparison.Ordinal))
        {
            return;
        }

        var method = _hubMethods.FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal));
        if (method is null)
        {
            return;
        }

        _connectionState.TotalReceivedMessages++;
        method.ReceivedCount++;
        MethodListenerChanged?.Invoke(method);
        AddMessage("Received", $"{method.DisplayName}: {argumentDisplay}", MessageType.Received, argumentDisplay);
    }

    /// <summary>
    /// Receives connection status changes from JavaScript callbacks.
    /// </summary>
    [JSInvokable("OnConnectionStatusChanged")]
    public void OnConnectionStatusChanged(string status)
    {
        if (_disposed)
        {
            return;
        }

        _connectionState.Status = status;
        if (status == "Disconnected")
        {
            _connectionState.ConnectionId = string.Empty;
            ResetMethodListeners();
        }
        AddMessage("System", T("UISignalR:DebugClient:ConnectionStatusChanged", GetLocalizedStatus(status)), MessageType.System);
        RaiseConnectionStateChanged();
    }

    /// <summary>Receives a browser status callback only while its connection generation remains current.</summary>
    /// <param name="status">The SignalR connection status.</param>
    /// <param name="connectionGeneration">The originating connection generation.</param>
    [JSInvokable("OnConnectionStatusChangedForSession")]
    public void OnConnectionStatusChangedForSession(string status, long connectionGeneration)
    {
        if (connectionGeneration == Volatile.Read(ref _connectionGeneration))
        {
            OnConnectionStatusChanged(status);
        }
    }

    /// <summary>
    /// Receives connection identifier changes from JavaScript callbacks.
    /// </summary>
    [JSInvokable("SetConnectionId")]
    public void SetConnectionId(string connectionId)
    {
        if (_disposed)
        {
            return;
        }

        _connectionState.ConnectionId = connectionId;
        RaiseConnectionStateChanged();
    }

    /// <summary>Receives a browser connection identifier only while its connection generation remains current.</summary>
    /// <param name="connectionId">The identifier supplied by SignalR.</param>
    /// <param name="connectionGeneration">The originating connection generation.</param>
    [JSInvokable("SetConnectionIdForSession")]
    public void SetConnectionIdForSession(string connectionId, long connectionGeneration)
    {
        if (connectionGeneration == Volatile.Read(ref _connectionGeneration))
        {
            SetConnectionId(connectionId);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        await _sessionGate.WaitAsync();

        try
        {
            var session = _session;
            var dotNetRef = _dotNetRef;
            var module = _module;

            _session = null;
            _dotNetRef = null;
            _module = null;

            if (session is not null && dotNetRef is not null && module is not null)
            {
                await DisposeOwnedSessionAsync(session, dotNetRef, module);
                return;
            }

            dotNetRef?.Dispose();
            if (session is not null)
            {
                await DisposeInteropReferenceAsync(session);
            }

            if (module is not null)
            {
                await DisposeInteropReferenceAsync(module);
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<JsonElement?> InvokeSessionAsync(string identifier, params object?[] arguments)
    {
        if (_disposed)
        {
            return null;
        }

        await _sessionGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return null;
            }

            var session = _session
                ?? throw new InvalidOperationException("The SignalR browser session has not been initialized.");
            var result = await session.InvokeAsync<JsonElement>(identifier, arguments);
            return _disposed ? null : result;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private Dictionary<string, string> BuildJavaScriptLocalization() => new()
    {
        ["NoArguments"] = T("UISignalR:JavaScript:NoArguments"),
        ["NotConnected"] = T("UISignalR:JavaScript:NotConnected"),
        ["ConnectionClosedWithError"] = T("UISignalR:JavaScript:ConnectionClosedWithError"),
        ["ConnectionClosed"] = T("UISignalR:JavaScript:ConnectionClosed"),
        ["Reconnecting"] = T("UISignalR:JavaScript:Reconnecting"),
        ["ReconnectedSuccessfully"] = T("UISignalR:JavaScript:ReconnectedSuccessfully"),
        ["ConnectedSuccessfully"] = T("UISignalR:JavaScript:ConnectedSuccessfully"),
        ["ConnectionFailed"] = T("UISignalR:JavaScript:ConnectionFailed"),
        ["Disconnected"] = T("UISignalR:JavaScript:Disconnected"),
        ["RegisteredListener"] = T("UISignalR:JavaScript:RegisteredListener"),
        ["UnregisteredListener"] = T("UISignalR:JavaScript:UnregisteredListener")
    };

    private static async ValueTask DisposeInteropReferenceAsync(IJSObjectReference reference)
    {
        try
        {
            await reference.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    private static async Task DisposeOwnedSessionAsync(
        IJSObjectReference session,
        DotNetObjectReference<SignalRDebugJsClient> dotNetRef,
        IJSObjectReference module)
    {
        await DisposeOwnedSessionAsync(
            session,
            dotNetRef.Dispose,
            module,
            SHUTDOWN_DRAIN_TIMEOUT);
    }

    internal static async Task DisposeOwnedSessionAsync(
        IJSObjectReference session,
        Action releaseCallbackReference,
        IJSObjectReference module,
        TimeSpan drainTimeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(releaseCallbackReference);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentOutOfRangeException.ThrowIfLessThan(drainTimeout, TimeSpan.Zero);

        var callbackReleaseTask = DrainAndReleaseCallbackAsync(session, releaseCallbackReference);

        try
        {
            try
            {
                await callbackReleaseTask.WaitAsync(drainTimeout);
            }
            catch (TimeoutException)
            {
                // Keep the callback reference owned by the drain continuation. The public disposal path
                // remains bounded while a late acknowledgement can still release the callback safely.
                _ = ObserveDeferredCallbackReleaseAsync(callbackReleaseTask);
            }
        }
        finally
        {
            // These handles no longer admit operations once disposal owns the session. Releasing them here
            // prevents an active-circuit handle leak even when callback acknowledgement is delayed.
            await DisposeInteropReferenceAsync(session);
            await DisposeInteropReferenceAsync(module);
        }
    }

    private static async Task DrainAndReleaseCallbackAsync(
        IJSObjectReference session,
        Action releaseCallbackReference)
    {
        try
        {
            // Supplying CancellationToken.None bypasses JSRuntime.DefaultAsyncTimeout. A caller-side bounded
            // wait must never cancel the underlying acknowledgement because JavaScript may still be draining
            // callbacks after that wait expires.
            var shutdownResult = await session.InvokeAsync<JsonElement>(
                "shutdown",
                CancellationToken.None);
            if (shutdownResult.ValueKind is not JsonValueKind.Object
                || !shutdownResult.TryGetProperty("drained", out var drained)
                || drained.ValueKind is not JsonValueKind.True)
            {
                return;
            }
        }
        catch (JSDisconnectedException)
        {
            // A disconnected circuit cannot dispatch another callback through this reference.
        }
        catch (TaskCanceledException)
        {
            // Cancellation is not proof that JavaScript drained its callbacks. Retain the reference rather
            // than turning an ambiguous cancellation into a use-after-dispose race.
            return;
        }
        catch (OperationCanceledException)
        {
            // Preserve the same ownership rule for non-task cancellation implementations.
            return;
        }

        releaseCallbackReference();
    }

    private static async Task ObserveDeferredCallbackReleaseAsync(Task callbackReleaseTask)
    {
        try
        {
            await callbackReleaseTask;
        }
        catch (JSException)
        {
            // A failed acknowledgement is not sufficient evidence to release the callback reference.
        }
        catch (OperationCanceledException)
        {
            // Cancellation is likewise unconfirmed; the callback reference remains retained for safety.
        }
    }

    private void RaiseConnectionStateChanged()
    {
        ConnectionStateChanged?.Invoke(GetConnectionState());
    }

    private void ResetMethodListeners()
    {
        foreach (var method in _methodsByHubRoute.Values.SelectMany(methods => methods.Values))
        {
            method.IsListening = false;
        }
    }

    private void AddMessage(string source, string content, MessageType type, string? details = null)
    {
        var message = new SignalRMessage
        {
            Source = source,
            Content = content,
            Details = details ?? string.Empty,
            Type = type,
            Timestamp = DateTime.UtcNow,
            IsError = type == MessageType.Error
        };

        _messages.Insert(0, message);
        if (_messages.Count > MAX_MESSAGE_COUNT)
        {
            _messages.RemoveAt(_messages.Count - 1);
        }

        MessageReceived?.Invoke(message);
    }

    private static string? ReadErrorDetails(JsonElement result) => result.TryGetProperty("details", out var details) ? details.GetString() : null;

    private string T(string key)
    {
        return localizer[key].Value;
    }

    private string T(string key, params object[] arguments)
    {
        return localizer[key, arguments].Value;
    }

    private string GetLocalizedStatus(string status)
    {
        return status switch
        {
            "Connected" => T("UISignalR:Common:Status:Connected"),
            "Connecting" => T("UISignalR:Common:Status:Connecting"),
            "Reconnecting" => T("UISignalR:Common:Status:Reconnecting"),
            "Disconnected" => T("UISignalR:Common:Status:Disconnected"),
            "Disconnecting" => T("UISignalR:Common:Status:Disconnecting"),
            _ => T("UISignalR:Common:Status:Unknown")
        };
    }
}
