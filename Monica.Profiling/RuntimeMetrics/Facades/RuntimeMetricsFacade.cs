using Monica.Core.Extensions;
using Microsoft.Extensions.Logging;
using Monica.Profiling.RuntimeMetrics.Services;
using Monica.Core.Results;
using Monica.Profiling.RuntimeMetrics.Models;

namespace Monica.Profiling.RuntimeMetrics.Facades;

/// <summary>
/// Provides result-envelope entry points for runtime metrics consumed by APIs and UI pages.
/// </summary>
public sealed class RuntimeMetricsFacade
{
    private readonly RuntimeMetricsService _runtimeMetricsService;
    private readonly ILogger<RuntimeMetricsFacade> _logger;

    internal RuntimeMetricsFacade(
        RuntimeMetricsService runtimeMetricsService,
        ILogger<RuntimeMetricsFacade> logger)
    {
        _runtimeMetricsService = runtimeMetricsService;
        _logger = logger;
    }

    public Result<RuntimeMetricsPoint> GetLatestPoint()
    {
        try
        {
            return Result.Success(_runtimeMetricsService.GetLatestPoint());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the latest runtime metrics point.");
            return Result.Failure<RuntimeMetricsPoint>($"Failed to load runtime metrics: {ex.GetMessageRecursively()}");
        }
    }

    public Result<RuntimeMetricsTrend> GetTrend()
    {
        try
        {
            return Result.Success(_runtimeMetricsService.GetTrend());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load runtime metrics trend data.");
            return Result.Failure<RuntimeMetricsTrend>($"Failed to load runtime metrics trend: {ex.GetMessageRecursively()}");
        }
    }

    public Action SubscribeToUpdates(Action<RuntimeMetricsPoint> callback)
    {
        return _runtimeMetricsService.SubscribeToUpdates(callback);
    }
}
