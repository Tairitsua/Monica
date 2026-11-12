using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.Utilities.Connectivity.Models;
using Monica.Utilities.Connectivity.Services;
using Monica.Utilities.Localization;

namespace Monica.Utilities.Connectivity.Facades;

/// <summary>
/// Result-envelope entry point for connectivity diagnostics consumed by UI pages or APIs.
/// </summary>
public sealed class ConnectivityProbeFacade
{
    private readonly ConnectivityProbeService _connectivityProbeService;
    private readonly IStringLocalizer<UtilitiesResource> _localizer;
    private readonly ILogger<ConnectivityProbeFacade> _logger;

    /// <summary>
    /// Creates the connectivity probe facade.
    /// </summary>
    /// <param name="connectivityProbeService">Service that executes DNS, ICMP, TCP, and HTTP probe logic.</param>
    /// <param name="localizer">Localizer used for developer-facing result messages.</param>
    /// <param name="logger">Logger used to record unexpected failures.</param>
    public ConnectivityProbeFacade(
        ConnectivityProbeService connectivityProbeService,
        IStringLocalizer<UtilitiesResource> localizer,
        ILogger<ConnectivityProbeFacade> logger)
    {
        _connectivityProbeService = connectivityProbeService;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>
    /// Runs a connectivity probe and wraps the structured result in Monica's result envelope.
    /// Expected network failures remain part of the returned result data instead of being converted into a failed envelope.
    /// </summary>
    /// <param name="request">Probe request describing the target and protocol kind.</param>
    /// <param name="cancellationToken">Cancellation token that aborts the probe.</param>
    /// <returns>A result envelope containing the probe result.</returns>
    public async Task<Result<ConnectivityProbeResult>> ProbeAsync(
        ConnectivityProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result.Success(await _connectivityProbeService.ProbeAsync(request, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute utilities connectivity probe for host {Host}.", request.Host);
            return Result.Failure<ConnectivityProbeResult>(
                _localizer["ServiceMessages:ConnectivityProbeFailed", ex.GetMessageRecursively()].Value,
                GetStatus(ex));
        }
    }

    private static ResultStatus GetStatus(Exception exception)
    {
        return exception switch
        {
            ArgumentException => ResultStatus.BadRequest,
            InvalidOperationException => ResultStatus.BadRequest,
            OperationCanceledException => ResultStatus.BadRequest,
            _ => ResultStatus.InternalError
        };
    }
}
