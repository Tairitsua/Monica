using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.SignalR.Metrics;
using Monica.SignalR.Models;

namespace Monica.SignalR.Services;

/// <summary>Sends an explicitly requested diagnostic callback to one browser connection.</summary>
internal sealed class SignalRDebugCallbackService(
    IServiceProvider services,
    IOptions<ModuleSignalROption> options,
    SignalRSendMetrics metrics)
{
    public Task SendAsync(string hubRoute, string connectionId, string methodName, object?[] arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        var registration = options.Value.HubRegistrations.SingleOrDefault(hub => hub.HubRoute == hubRoute)
            ?? throw new ArgumentException("The diagnostic callback requires a mapped hub route.", nameof(hubRoute));
        var method = registration.HubType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SingleOrDefault(candidate => candidate.Name == methodName && candidate.GetParameters().Length == arguments.Length)
            ?? throw new ArgumentException("The callback must match a declared hub method and its argument count.", nameof(methodName));
        return (Task)typeof(SignalRDebugCallbackService).GetMethod(nameof(SendCoreAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(registration.HubType)
            .Invoke(this, [connectionId, method.Name, arguments, cancellationToken])!;
    }

    private async Task SendCoreAsync<THub>(string connectionId, string methodName, object?[] arguments,
        CancellationToken cancellationToken) where THub : Hub
    {
        var client = services.GetRequiredService<IHubContext<THub>>().Clients.Client(connectionId);
        var observation = metrics.StartObservedSend(typeof(THub).Name, methodName,
            SignalRSendTarget.Create(SignalRSendTargetKind.Client, connectionId, metrics.IncludeTargetIdentifiers));
        try
        {
            await client.SendCoreAsync(methodName, arguments, cancellationToken);
            observation.Complete();
        }
        catch (Exception exception)
        {
            observation.Complete(exception);
            throw;
        }
    }
}
