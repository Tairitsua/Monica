using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.DevOps.Localization;
using Monica.DevOps.Terminal.Models;
using Monica.DevOps.Terminal.Services;

namespace Monica.DevOps.Terminal.Facades;

/// <summary>
/// Public terminal entry point used by the DevOps terminal UI.
/// </summary>
public sealed class TerminalFacade(
    TerminalSessionService sessionService,
    ILogger<TerminalFacade> logger,
    IStringLocalizer<TerminalResource> localizer)
{
    /// <summary>
    /// Gets the local terminal environment.
    /// </summary>
    /// <returns>The detected environment information.</returns>
    public Task<Result<TerminalEnvironmentSnapshot>> GetEnvironmentAsync()
        => ExecuteAsync(
            () => Task.FromResult(sessionService.GetEnvironment()),
            localizer["Messages:LoadEnvironmentFailed"].Value);

    /// <summary>
    /// Gets active terminal sessions for the requested owner.
    /// </summary>
    /// <param name="ownerKey">Stable owner key that isolates browser or user terminal sessions.</param>
    /// <returns>The active sessions.</returns>
    public Task<Result<IReadOnlyList<TerminalSessionSnapshot>>> GetSessionsAsync(string ownerKey)
        => ExecuteAsync(
            () => Task.FromResult(sessionService.GetSessions(ownerKey)),
            localizer["Messages:LoadSessionsFailed"].Value);

    /// <summary>
    /// Creates a terminal session for the requested owner.
    /// </summary>
    /// <param name="ownerKey">Stable owner key that isolates browser or user terminal sessions.</param>
    /// <param name="request">Session creation request.</param>
    /// <returns>The created session.</returns>
    public Task<Result<TerminalSessionSnapshot>> CreateSessionAsync(string ownerKey, TerminalSessionCreateRequest? request = null)
        => ExecuteAsync(
            () => Task.FromResult(sessionService.CreateSession(ownerKey, request)),
            localizer["Messages:CreateSessionFailed"].Value);

    /// <summary>
    /// Closes an owner-scoped terminal session.
    /// </summary>
    /// <param name="ownerKey">Stable owner key that isolates browser or user terminal sessions.</param>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns>Removal and cancellation-request receipt; this does not prove process drainage.</returns>
    public Task<Result> CloseSessionAsync(string ownerKey, string sessionId)
        => ExecuteAsync(
            () => sessionService.CloseSessionAsync(ownerKey, sessionId),
            localizer["Messages:SessionClosed"].Value,
            localizer["Messages:CloseSessionFailed"].Value);

    /// <summary>
    /// Cancels the current command in an owner-scoped terminal session.
    /// </summary>
    /// <param name="ownerKey">Stable owner key that isolates browser or user terminal sessions.</param>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns>Operation result.</returns>
    /// <param name="expectedCommandId">Frozen active command identity; stale targets fail without canceling a newer run.</param>
    /// <remarks>Request completion does not prove command termination. Observe IsRunning in later snapshots.</remarks>
    public Task<Result<TerminalSessionSnapshot>> CancelCommandAsync(string ownerKey, string sessionId, string expectedCommandId)
        => ExecuteAsync(
            () => sessionService.CancelCommandAsync(ownerKey, sessionId, expectedCommandId),
            localizer["Messages:CancelFailed"].Value);

    /// <summary>Accepts host-owned execution independently of the calling UI visit.</summary>
    /// <remarks>Success means admitted, not successful process completion. Observe session snapshots by
    /// the receipt's CommandId. No visit token cancels the admitted command.</remarks>
    public Task<Result<TerminalCommandAcceptance>> StartCommandAsync(string ownerKey, string sessionId, TerminalCommandRequest request)
        => ExecuteAsync(
            () => Task.FromResult(sessionService.StartCommand(ownerKey, sessionId, request)),
            localizer["Messages:RunCommandFailed"].Value);

    /// <summary>
    /// Clears retained output for an owner-scoped terminal session.
    /// </summary>
    /// <param name="ownerKey">Stable owner key that isolates browser or user terminal sessions.</param>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns>Operation result.</returns>
    public Task<Result> ClearOutputAsync(string ownerKey, string sessionId)
        => ExecuteAsync(
            () =>
            {
                sessionService.ClearOutput(ownerKey, sessionId);
                return Task.CompletedTask;
            },
            localizer["Messages:OutputCleared"].Value,
            localizer["Messages:ClearOutputFailed"].Value);

    /// <summary>
    /// Executes a command in an owner-scoped terminal session.
    /// </summary>
    /// <param name="ownerKey">Stable owner key that isolates browser or user terminal sessions.</param>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="request">Command request.</param>
    /// <param name="outputCallback">Optional serial observer; actual async callbacks are joined and failures observed.</param>
    /// <param name="ct">Explicit execution cancellation; do not pass a route-lifetime token for continued commands.</param>
    /// <returns>The command result after actual process, output and callback drainage.</returns>
    public Task<Result<TerminalCommandResult>> ExecuteCommandAsync(
        string ownerKey,
        string sessionId,
        TerminalCommandRequest request,
        Func<TerminalOutputLine, CancellationToken, ValueTask>? outputCallback = null,
        CancellationToken ct = default)
        => ExecuteAsync(
            () => sessionService.ExecuteCommandAsync(ownerKey, sessionId, request, outputCallback, ct),
            localizer["Messages:RunCommandFailed"].Value);

    private async Task<Result<T>> ExecuteAsync<T>(
        Func<Task<T>> action,
        string failureMessage)
    {
        try
        {
            return Result.Success(await action());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "{FailureMessage}", failureMessage);
            return Result.Failure<T>($"{failureMessage}: {ex.GetMessageRecursively()}", GetResultStatus(ex));
        }
    }

    private async Task<Result> ExecuteAsync(
        Func<Task> action,
        string successMessage,
        string failureMessage)
    {
        try
        {
            await action();
            return Result.Success().WithMessage(successMessage);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "{FailureMessage}", failureMessage);
            return Result.Failure($"{failureMessage}: {ex.GetMessageRecursively()}", GetResultStatus(ex));
        }
    }

    private static ResultStatus GetResultStatus(Exception exception)
    {
        return exception switch
        {
            ArgumentException => ResultStatus.BadRequest,
            InvalidOperationException => ResultStatus.BadRequest,
            KeyNotFoundException => ResultStatus.BadRequest,
            DirectoryNotFoundException => ResultStatus.BadRequest,
            UnauthorizedAccessException => ResultStatus.Forbidden,
            _ => ResultStatus.InternalError
        };
    }
}
