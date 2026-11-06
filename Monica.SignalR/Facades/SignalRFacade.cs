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
