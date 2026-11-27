using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Monica.Modules;
using Monica.SignalR.Abstractions;
using Monica.SignalR.Facades;
using Monica.SignalR.Localization;
using Monica.SignalR.Metrics;
using Monica.SignalR.Models;
using Monica.SignalR.Services;
using Monica.SignalR.Services.Support;
using Monica.SignalR.UISignalR.State;
using Monica.SignalR.UISignalR.Support;
using Xunit;

namespace Test.Monica.SignalR.UISignalR.Support;

public sealed class SignalRDebugSessionTests
{
    [Fact]
    public async Task SetHubMetadata_WhenRoutesShareMethodNames_ShouldKeepRouteIdentityAndRetainRefreshedListenerEvidence()
    {
        var session = new RecordingSession();
        await using var client = CreateClient(session);
        await client.InitializeAsync();
        client.SetHubMetadata([HubMetadata("/first", "First", "First label", "firstArgument"),
            HubMetadata("/second", "Second", "Second label", "secondArgument")], "/first");
        await client.ConnectAsync("https://host/first", string.Empty);
        await client.ToggleMethodListenerAsync("Echo", true);
        client.OnMethodReceived("Echo", "[1]", "/first", session.ConnectionGeneration);
        var original = client.GetHubMethods().Single();

        client.SetHubMetadata([HubMetadata("/first", "First", "Updated label", "firstArgument"),
            HubMetadata("/second", "Second", "Second label", "secondArgument")], "/first");
        client.OnMethodReceived("Echo", "[2]", "/first", session.ConnectionGeneration);

        var refreshed = client.GetHubMethods().Single();
        refreshed.Should().BeSameAs(original);
        refreshed.IsListening.Should().BeTrue();
        refreshed.ReceivedCount.Should().Be(2);
        client.GetMessages()[0].Content.Should().Be("Updated label (Echo): [2]");
        client.GetMessages()[0].Details.Should().Be("[2]");
        client.GetMessages()[0].Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        client.GetMessages().Select(message => message.Id).Should().OnlyHaveUniqueItems();

        client.SetHubMetadata([HubMetadata("/first", "First", "Updated label", "firstArgument"),
            HubMetadata("/second", "Second", "Second label", "secondArgument")], "/second");
        client.OnMethodReceived("Echo", "[3]", "/first", session.ConnectionGeneration);

        client.MethodCatalogHubRoute.Should().Be("/second");
        client.GetHubMethods().Single().Args.Single().Name.Should().Be("secondArgument");
        client.GetHubMethods().Single().ReceivedCount.Should().Be(0);
        client.GetConnectionState().TotalReceivedMessages.Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectAsync_WhenTransportShutdownCompletes_ShouldClearListenerFlagsAndRetainReceivedCounts(bool success)
    {
        var session = new RecordingSession { DisconnectSucceeds = success };
        await using var client = CreateClient(session);
        await client.InitializeAsync();
        client.SetHubMetadata([HubMetadata("/first", "First", "Echo", "value")], "/first");
        await client.ConnectAsync("https://host/first", string.Empty);
        await client.ToggleMethodListenerAsync("Echo", true);
        client.OnMethodReceived("Echo", "[1]", "/first", session.ConnectionGeneration);
        session.DisconnectGate = NewGate();

        var disconnect = client.DisconnectAsync();
        client.GetConnectionState().Status.Should().Be("Disconnecting");
        session.DisconnectGate!.SetResult();
        (await disconnect).Should().Be(success);

        client.GetConnectionState().Status.Should().Be("Disconnected");
        client.GetConnectionState().ConnectionId.Should().BeEmpty();
        client.GetHubMethods().Single().IsListening.Should().BeFalse();
        client.GetHubMethods().Single().ReceivedCount.Should().Be(1);
        await client.ConnectAsync("https://host/first", string.Empty);
        client.GetHubMethods().Single().IsListening.Should().BeFalse();
        client.GetHubMethods().Single().ReceivedCount.Should().Be(1);
    }

    [Fact]
    public async Task ConnectAsync_WhenOldRegistrationAndCallbacksArriveLate_ShouldRejectReplacedConnectionEvidence()
    {
        var session = new RecordingSession();
        await using var client = CreateClient(session);
        await client.InitializeAsync();
        client.SetHubMetadata([HubMetadata("/first", "First", "Echo", "value")], "/first");
        await client.ConnectAsync("https://host/first", string.Empty);
        var oldGeneration = session.ConnectionGeneration;
        session.ListenerGate = NewGate();
        var registration = client.ToggleMethodListenerAsync("Echo", true);

        var reconnect = client.ConnectAsync("https://host/first", string.Empty);
        session.ListenerGate!.SetResult();
        (await registration).Should().BeFalse();
        (await reconnect).Should().BeTrue();
        client.OnMethodReceived("Echo", "[1]", "/first", oldGeneration);
        client.OnConnectionStatusChangedForSession("Disconnected", oldGeneration);
        client.SetConnectionIdForSession("old-connection", oldGeneration);

        client.GetHubMethods().Single().IsListening.Should().BeFalse();
        client.GetHubMethods().Single().ReceivedCount.Should().Be(0);
        client.GetConnectionState().Status.Should().Be("Connected");
        client.GetConnectionState().ConnectionId.Should().Be("connection-2");
    }

    [Fact]
    public async Task ConnectAsync_WhenDraftRouteChangesDuringConnect_ShouldKeepActiveTargetUntilDisconnect()
    {
        await using var scenario = new StateScenario();
        await scenario.State.InitializeAsync();
        scenario.State.SelectMethod("Echo");
        scenario.State.MethodParameters["value"] = "12";
        scenario.Session.ConnectGate = NewGate();

        var connect = scenario.State.ConnectAsync("https://host/app/");
        scenario.State.ConnectionState.Status.Should().Be("Connecting");
        scenario.State.ConnectedHubRoute.Should().Be("/first");
        scenario.State.SelectedHubRoute = "/second";
        scenario.State.MethodCatalogHubRoute.Should().Be("/first");
        scenario.Session.LastHubUrl.Should().Be("https://host/app/first");
        scenario.State.MethodParameters["value"].Should().Be("12");

        scenario.Session.ConnectGate!.SetResult();
        (await connect).Should().BeTrue();
        scenario.State.ConnectedHubRoute.Should().Be("/first");
        scenario.State.MethodCatalogHubRoute.Should().Be("/first");
        scenario.State.SelectedHubRoute.Should().Be("/second");

        scenario.Session.DisconnectGate = NewGate();
        var disconnect = scenario.State.DisconnectAsync();
        scenario.State.ConnectionState.Status.Should().Be("Disconnecting");
        scenario.State.MethodCatalogHubRoute.Should().Be("/first");
        scenario.Session.DisconnectGate!.SetResult();
        (await disconnect).Should().BeTrue();

        scenario.State.ConnectedHubRoute.Should().BeEmpty();
        scenario.State.MethodCatalogHubRoute.Should().Be("/second");
        scenario.State.HubMethods.Single().Args.Single().Name.Should().Be("message");
        scenario.State.SelectedMethodName.Should().BeEmpty();
        scenario.State.MethodParameters.Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectAsync_WhenReconnectingToDraftTarget_ShouldSwitchCatalogAndResetActualListenerFlags()
    {
        await using var scenario = new StateScenario();
        await scenario.State.InitializeAsync();
        await scenario.State.ConnectAsync("https://host/");
        var firstMethod = scenario.State.HubMethods.Single();
        await scenario.State.ToggleMethodListenerAsync(firstMethod, true);
        scenario.Client.OnMethodReceived("Echo", "[1]", "/first", scenario.Session.ConnectionGeneration);
        scenario.State.SelectedHubRoute = "/second";

        (await scenario.State.ConnectAsync("https://host/")).Should().BeTrue();

        scenario.State.ConnectedHubRoute.Should().Be("/second");
        scenario.State.MethodCatalogHubRoute.Should().Be("/second");
        scenario.Session.LastHubUrl.Should().Be("https://host/second");
        firstMethod.IsListening.Should().BeFalse();
        firstMethod.ReceivedCount.Should().Be(1);
        scenario.State.HubMethods.Single().IsListening.Should().BeFalse();
        scenario.State.HubMethods.Single().ReceivedCount.Should().Be(0);
    }

    [Fact]
    public async Task LoadSnapshots_WhenReadsFail_ShouldKeepLastEvidenceAndRecoverToSuccessfulEmptyCaptures()
    {
        await using var scenario = new StateScenario();
        scenario.Registry.AddConnection("connection-1", AuthenticatedUser());
        await scenario.State.InitializeAsync();
        var hubsCapturedAt = scenario.State.HubsCapturedAtUtc;
        var usersCapturedAt = scenario.State.ConnectedUsersCapturedAtUtc;
        var diagnostics = scenario.State.SendDiagnostics;
        scenario.FacadeServices.IsAvailable = false;

        (await scenario.State.LoadHubsAsync()).Should().BeFalse();
        (await scenario.State.LoadConnectedUsersAsync()).Should().BeFalse();
        (await scenario.State.LoadSendDiagnosticsAsync()).Should().BeFalse();

        scenario.State.HubsLoaded.Should().BeTrue();
        scenario.State.HubsLoadFailed.Should().BeTrue();
        scenario.State.IsLoadingHubs.Should().BeFalse();
        scenario.State.AvailableHubs.Should().HaveCount(2);
        scenario.State.HubsCapturedAtUtc.Should().Be(hubsCapturedAt);
        scenario.State.ConnectedUsersLoaded.Should().BeTrue();
        scenario.State.ConnectedUsersLoadFailed.Should().BeTrue();
        scenario.State.IsLoadingConnectedUsers.Should().BeFalse();
        scenario.State.ConnectedUsers.Should().ContainSingle();
        scenario.State.ConnectedUsersCapturedAtUtc.Should().Be(usersCapturedAt);
        scenario.State.SendDiagnosticsLoadFailed.Should().BeTrue();
        scenario.State.IsLoadingSendDiagnostics.Should().BeFalse();
        scenario.State.SendDiagnostics.Should().BeSameAs(diagnostics);

        scenario.FacadeServices.IsAvailable = true;
        scenario.SignalROptions.HubRegistrations.Clear();
        scenario.Registry.RemoveConnection("connection-1");
        (await scenario.State.LoadHubsAsync()).Should().BeTrue();
        (await scenario.State.LoadConnectedUsersAsync()).Should().BeTrue();
        (await scenario.State.LoadSendDiagnosticsAsync()).Should().BeTrue();

        scenario.State.AvailableHubs.Should().BeEmpty();
        scenario.State.HubsLoaded.Should().BeTrue();
        scenario.State.HubsLoadFailed.Should().BeFalse();
        scenario.State.HubsCapturedAtUtc.Should().NotBeNull();
        scenario.State.ConnectedUsers.Should().BeEmpty();
        scenario.State.ConnectedUsersLoaded.Should().BeTrue();
        scenario.State.ConnectedUsersLoadFailed.Should().BeFalse();
        scenario.State.SendDiagnosticsLoadFailed.Should().BeFalse();
        scenario.State.SendDiagnostics!.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task RegistryDiagnostic_WhenConnectionIsAbsent_ShouldReportOnlySuccessfulCaptureAbsence()
    {
        await using var scenario = new StateScenario();
        await scenario.State.InitializeAsync();
        await scenario.State.ConnectAsync("https://host/");
        scenario.State.IsConnectionNotObserved.Should().BeTrue();

        scenario.FacadeServices.IsAvailable = false;
        await scenario.State.LoadConnectedUsersAsync();
        scenario.State.IsConnectionNotObserved.Should().BeFalse();
        scenario.FacadeServices.IsAvailable = true;
        scenario.Registry.AddConnection("connection-1", AuthenticatedUser());
        await scenario.State.LoadConnectedUsersAsync();
        scenario.State.IsConnectionNotObserved.Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_WhenImportArrivesAfterRetirement_ShouldDisposeLateModuleWithoutCreatingSession()
    {
        var session = new RecordingSession();
        var module = new RecordingModule(session);
        var import = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingRuntime(module, import.Task);
        var client = new SignalRDebugJsClient(runtime, new SignalRInvocationArgumentParser(), new KeyLocalizer());
        var initialization = client.InitializeAsync();

        var disposal = client.DisposeAsync().AsTask();
        import.SetResult(module);
        await initialization;
        await disposal;

        module.IsDisposed.Should().BeTrue();
        module.SessionCreated.Should().BeFalse();
        session.IsDisposed.Should().BeFalse();
    }

    private static SignalRDebugJsClient CreateClient(RecordingSession session) => new(
        new RecordingRuntime(new RecordingModule(session)), new SignalRInvocationArgumentParser(), new KeyLocalizer());

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SignalRHubInfo HubMetadata(string route, string hub, string description, string argument) => new()
    {
        HubName = hub,
        Route = route,
        Methods = [new SignalRHubMethodInfo
        {
            Name = "Echo",
            Description = description,
            Parameters = [new SignalRHubParameterInfo { Name = argument, TypeName = "Int32" }]
        }]
    };

    private static ClaimsPrincipal AuthenticatedUser() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "test-user")], "test"));

    private sealed class StateScenario : IAsyncDisposable
    {
        private readonly ServiceProvider _resources;

        public StateScenario()
        {
            _resources = new ServiceCollection().AddMetrics().BuildServiceProvider();
            SignalROptions.HubRegistrations.Add(new SignalRHubRegistration(typeof(FirstHub), "/first"));
            SignalROptions.HubRegistrations.Add(new SignalRHubRegistration(typeof(SecondHub), "/second"));
            var inspection = new SignalRInspectionService(Options.Create(SignalROptions), new SignalRHubMetadataReader(),
                Registry, new SignalRSendMetrics(_resources.GetRequiredService<IMeterFactory>(), Options.Create(SignalROptions)));
            FacadeServices = new SwitchableInspectionProvider(inspection);
            Client = CreateClient(Session);
            var uiOptions = Options.Create(new ModuleSignalRUIOption());
            State = new SignalRDebugPageState(uiOptions, new SignalRFacade(NullLogger<SignalRFacade>.Instance, FacadeServices),
                Client, new SignalRDebugTestTokenService(_resources, uiOptions, new TestEnvironment()));
        }

        public ModuleSignalROption SignalROptions { get; } = new();
        public RecordingRegistry Registry { get; } = new();
        public RecordingSession Session { get; } = new();
        public SwitchableInspectionProvider FacadeServices { get; }
        public SignalRDebugJsClient Client { get; }
        public SignalRDebugPageState State { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await State.DisposeAsync();
            }
            finally
            {
                await _resources.DisposeAsync();
            }
        }
    }

    private sealed class SwitchableInspectionProvider(SignalRInspectionService inspection) : IServiceProvider
    {
        public bool IsAvailable { get; set; } = true;
        public object? GetService(Type serviceType) => IsAvailable && serviceType == typeof(SignalRInspectionService) ? inspection : null;
    }

    private sealed class RecordingRegistry : ISignalRConnectionRegistry
    {
        private readonly Dictionary<string, SignalRConnectionInfo> _connections = new(StringComparer.Ordinal);
        public void AddConnection(string connectionId, ClaimsPrincipal claimsPrincipal) => _connections[connectionId] = new()
        {
            ConnectionId = connectionId,
            ClaimsPrincipal = claimsPrincipal
        };
        public void RemoveConnection(string connectionId) => _connections.Remove(connectionId);
        public IReadOnlyList<SignalRConnectionInfo> GetConnectionInfos() => _connections.Values.ToList();
        public SignalRConnectionInfo? GetConnectionInfo(string connectionId) => _connections.GetValueOrDefault(connectionId);
    }

    private sealed class FirstHub : Hub
    {
        public Task Echo(int value) => Task.CompletedTask;
    }

    private sealed class SecondHub : Hub
    {
        public Task Echo(string message) => Task.CompletedTask;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "SignalRDebugSessionTests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class KeyLocalizer : IStringLocalizer<SignalRResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, $"{name}: {string.Join(", ", arguments)}");
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class RecordingRuntime(RecordingModule module, Task<IJSObjectReference>? import = null) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            identifier.Should().Be("import");
            var imported = import is null ? module : await import;
            return (TValue)(object)imported;
        }
    }

    private sealed class RecordingModule(RecordingSession session) : IJSObjectReference
    {
        public bool IsDisposed { get; private set; }
        public bool SessionCreated { get; private set; }
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            identifier.Should().Be("createSession");
            SessionCreated = true;
            return ValueTask.FromResult((TValue)(object)session);
        }
    }

    private sealed class RecordingSession : IJSObjectReference
    {
        public TaskCompletionSource? ConnectGate { get; set; }
        public TaskCompletionSource? DisconnectGate { get; set; }
        public TaskCompletionSource? ListenerGate { get; set; }
        public long ConnectionGeneration { get; private set; }
        public string? LastHubUrl { get; private set; }
        public bool DisconnectSucceeds { get; init; } = true;
        public bool IsDisposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "connect")
            {
                LastHubUrl = (string)args![0]!;
                ConnectionGeneration = (long)args[2]!;
                if (ConnectGate is not null)
                {
                    await ConnectGate.Task;
                }
            }
            if (identifier == "disconnect" && DisconnectGate is not null)
            {
                await DisconnectGate.Task;
            }
            if (identifier == "registerListener" && ListenerGate is not null)
            {
                await ListenerGate.Task;
            }

            var result = identifier switch
            {
                "shutdown" => JsonSerializer.SerializeToElement(new { drained = true }),
                "connect" => JsonSerializer.SerializeToElement(new { success = true, connectionId = $"connection-{ConnectionGeneration}" }),
                "disconnect" when !DisconnectSucceeds => JsonSerializer.SerializeToElement(new { success = false, error = "stop failed" }),
                _ => JsonSerializer.SerializeToElement(new { success = true })
            };
            return (TValue)(object)result;
        }
    }
}
