using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.SignalR.Models;
using Monica.SignalR.Services;

namespace Monica.SignalR.Facades;

/// <summary>
/// Public SignalR entry point used by Minimal API handlers and UI components.
/// </summary>
public sealed class SignalRFacade(
    ILogger<SignalRFacade> logger,
    IServiceProvider serviceProvider)
{
    /// <summary>Sends a diagnostic callback to one explicitly selected browser connection on a mapped hub.</summary>
    /// <remarks>
    /// This is a server-to-client probe, not a hub method invocation or a simulated receipt. The caller owns the
    /// connection selection and argument values. Completion confirms only the server send task; the browser trace
    /// separately proves receipt. The probe participates in enabled send metrics and identifier-redaction policy.
    /// No HTTP endpoint is contributed for this operation.
    /// </remarks>
    /// <param name="hubRoute">A route mapped through the SignalR module.</param>
    /// <param name="connectionId">The debug browser's current connection identifier.</param>
    /// <param name="methodName">A declared hub method name with the supplied argument count.</param>
    /// <param name="arguments">Callback arguments to serialize through the host's SignalR protocol.</param>
    /// <param name="cancellationToken">Cancels the server send without claiming browser receipt.</param>
    public async Task<Result> SendDebugCallbackAsync(string hubRoute, string connectionId, string methodName,
        object?[] arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            await serviceProvider.GetRequiredService<SignalRDebugCallbackService>()
                .SendAsync(hubRoute, connectionId, methodName, arguments, cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to send a diagnostic SignalR callback.");
            return Result.Failure(exception.GetMessageRecursively(), ResultStatus.InternalError);
        }
    }

    /// <summary>
    /// Gets metadata for all registered SignalR hubs.
    /// </summary>
    public Task<Result<List<SignalRHubInfo>>> GetHubInfosAsync()
    {
        try
        {
            var inspectionService = serviceProvider.GetRequiredService<SignalRInspectionService>();
            return Task.FromResult(Result.Success(inspectionService.GetHubInfos().ToList()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get SignalR hub metadata.");
            return Task.FromResult<Result<List<SignalRHubInfo>>>(Result.Failure<List<SignalRHubInfo>>(
                $"Failed to get SignalR hub metadata: {ex.GetMessageRecursively()}",
                ResultStatus.InternalError));
        }
    }

    /// <summary>
    /// Gets the currently connected SignalR users.
    /// </summary>
    public Task<Result<List<SignalRConnectedUserInfo>>> GetConnectedUsersAsync()
    {
        try
        {
            var inspectionService = serviceProvider.GetRequiredService<SignalRInspectionService>();
            return Task.FromResult(Result.Success(inspectionService.GetConnectedUsers().ToList()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get connected SignalR users.");
            return Task.FromResult<Result<List<SignalRConnectedUserInfo>>>(Result.Failure<List<SignalRConnectedUserInfo>>(
                $"Failed to get connected SignalR users: {ex.GetMessageRecursively()}",
                ResultStatus.InternalError));
        }
    }

    /// <summary>
    /// Gets Monica-observed SignalR server-to-client send diagnostics.
    /// </summary>
    public Task<Result<SignalRSendDiagnosticsSnapshot>> GetSendDiagnosticsAsync()
    {
        try
        {
            var inspectionService = serviceProvider.GetRequiredService<SignalRInspectionService>();
            return Task.FromResult(Result.Success(inspectionService.GetSendDiagnostics()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get SignalR send diagnostics.");
            return Task.FromResult<Result<SignalRSendDiagnosticsSnapshot>>(Result.Failure<SignalRSendDiagnosticsSnapshot>(
                $"Failed to get SignalR send diagnostics: {ex.GetMessageRecursively()}",
                ResultStatus.InternalError));
        }
    }
}
