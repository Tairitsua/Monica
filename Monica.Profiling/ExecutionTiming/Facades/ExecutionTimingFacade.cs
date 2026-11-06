using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.Profiling.ExecutionTiming.Models;
using Monica.Profiling.ExecutionTiming.Services;

namespace Monica.Profiling.ExecutionTiming.Facades;

/// <summary>
/// Provides result-envelope entry points for execution-timing diagnostics used by APIs and UI pages.
/// </summary>
public sealed class ExecutionTimingFacade
{
    private readonly ExecutionTimingService _executionTimingService;
    private readonly ILogger<ExecutionTimingFacade> _logger;

    internal ExecutionTimingFacade(
        ExecutionTimingService executionTimingService,
        ILogger<ExecutionTimingFacade> logger)
    {
        _executionTimingService = executionTimingService;
        _logger = logger;
    }

    public Result<IReadOnlyList<ExecutionTimingStatistics>> GetStatistics()
    {
        try
        {
            return Result.Success(_executionTimingService.GetStatistics());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load execution timing statistics.");
            return Result.Failure<IReadOnlyList<ExecutionTimingStatistics>>($"Failed to load execution timing statistics: {ex.GetMessageRecursively()}");
        }
    }

    public Result<IReadOnlyList<RunningExecutionTimingInfo>> GetRunningOperations()
    {
        try
        {
            return Result.Success(_executionTimingService.GetRunningOperations());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load running execution timing operations.");
            return Result.Failure<IReadOnlyList<RunningExecutionTimingInfo>>($"Failed to load running execution timing operations: {ex.GetMessageRecursively()}");
        }
    }
}
