using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.Results;
using Monica.Modules;
using Monica.UI.Localization;
using Monica.UI.UISystemInfo.Models;
using Monica.UI.UISystemInfo.Services;
using Monica.UI.Shared.Services;

namespace Monica.UI.UISystemInfo.Facades;

/// <summary>
/// Provides the UI and HTTP boundary for safe, point-in-time host diagnostics and the optional self-restart action.
/// </summary>
/// <param name="server">The host-owned server whose live feature collection publishes listening addresses.</param>
/// <param name="monicaApplication">The host-owned Monica application state.</param>
/// <param name="shellOptions">The application identity displayed by the Monica shell.</param>
/// <param name="timeFormatter">The host-owned Clock timezone used by Monica pages.</param>
/// <param name="hostEnvironment">The Generic Host environment.</param>
/// <param name="applicationLifetime">The Generic Host lifetime controller.</param>
/// <param name="systemInfoOptions">The system-information module options.</param>
/// <param name="localizer">The host-scoped system-information localizer.</param>
/// <param name="loggerFactory">The host logger factory.</param>
public sealed class SystemInfoFacade(
    IServer server,
    MonicaApplication monicaApplication,
    IOptions<ModuleShellUIOption> shellOptions,
    MoTimeFormatter timeFormatter,
    IHostEnvironment hostEnvironment,
    IHostApplicationLifetime applicationLifetime,
    IOptions<ModuleSystemInfoUIOption> systemInfoOptions,
    IStringLocalizer<SystemInfoResource> localizer,
    ILoggerFactory loggerFactory)
{
    private readonly SystemInfoSnapshotProvider _snapshotProvider = new(
        server,
        monicaApplication,
        shellOptions,
        timeFormatter,
        hostEnvironment);
    private readonly SystemInfoRestartCoordinator _restartCoordinator = new(
        applicationLifetime,
        systemInfoOptions,
        loggerFactory.CreateLogger<SystemInfoRestartCoordinator>());
    private readonly ILogger<SystemInfoFacade> _logger = loggerFactory.CreateLogger<SystemInfoFacade>();

    /// <summary>
    /// Captures one immutable system-information snapshot for the current host.
    /// </summary>
    /// <returns>A successful result containing the snapshot, or a localized failure result.</returns>
    public Res<SystemInfoSnapshot> GetSnapshot()
    {
        try
        {
            return Res.Ok(_snapshotProvider.Capture());
        }
        catch (SystemInfoUnavailableException ex)
        {
            _logger.LogWarning(ex, "System information is unavailable. Reason: {Reason}.", ex.Reason);
            var key = ex.Reason switch
            {
                SystemInfoUnavailableReason.BrowserRuntime => "Service:Errors:BrowserRuntimeUnsupported",
                SystemInfoUnavailableReason.EntryAssemblyUnavailable => "Service:Errors:EntryAssemblyUnavailable",
                _ => throw new ArgumentOutOfRangeException(nameof(ex.Reason), ex.Reason, null)
            };

            return Res.Fail(localizer[key].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture system information.");
            return Res.Fail(localizer["Service:Errors:GetSystemInfoFailed"].Value);
        }
    }

    /// <summary>
    /// Requests graceful host shutdown so an external supervisor can restart the process.
    /// </summary>
    /// <returns>A localized result describing whether shutdown was scheduled.</returns>
    public Res RequestSelfRestart()
    {
        try
        {
            var request = _restartCoordinator.Request();
            return request.Status switch
            {
                SystemInfoRestartStatus.Scheduled => Res.Ok(localizer[
                    "Service:Messages:RestartRequested",
                    request.Delay.TotalSeconds].Value),
                SystemInfoRestartStatus.Disabled => Res.Fail(localizer[
                    "Service:Errors:SelfRestartDisabled"].Value),
                SystemInfoRestartStatus.AlreadyPending => Res.Fail(localizer[
                    "Service:Errors:RestartAlreadyRequested"].Value),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Status), request.Status, null)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to schedule graceful shutdown for a self restart request.");
            return Res.Fail(localizer["Service:Errors:RestartSchedulingFailed"].Value);
        }
    }
}
