using Microsoft.Extensions.Logging;
using Monica.Core.ObservableInstance.Facades;
using Monica.Core.ObservableInstance.Models;
using Monica.Core.Results;
using Monica.Framework.UI.UIObservableInstance.Models;
using Monica.Tool.Extensions;

namespace Monica.Framework.UI.UIObservableInstance.Support;

/// <summary>
/// UI support service that maps observable instance facade results to UI view models.
/// </summary>
public sealed class ObservableInstanceMonitorService(
    ObservableInstanceFacade observableInstanceFacade,
    ILogger<ObservableInstanceMonitorService> logger)
{
    private readonly ObservableInstanceFacade _facade = observableInstanceFacade;

    /// <summary>Captures one registry membership read and all retained evidence without live value references.</summary>
    /// <param name="cancellationToken">Cancels the underlying tracker captures.</param>
    /// <returns>A detached capture; independent tracker reads are not globally atomic.</returns>
    public async Task<Result<ObservableInstanceCapture>> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var result = await _facade.CaptureAsync(cancellationToken);
        var error = result.WithoutData();
        var instances = result.Data;
        if (result.IsFailure()) return Result.FromFailure<ObservableInstanceCapture>(error);
        cancellationToken.ThrowIfCancellationRequested();
        return Result.Success(new ObservableInstanceCapture(DateTime.UtcNow, instances ?? []));
    }

    /// <summary>Clears retained records for the captured registration, preserving current state and lifetime counters.</summary>
    /// <param name="identity">The frozen registration identity to clear.</param>
    /// <param name="cancellationToken">Cancels before the side effect.</param>
    public Task<Result> ClearHistoryAsync(ObservableInstanceReference identity, CancellationToken cancellationToken = default)
        => _facade.ClearInstanceHistoryAsync(identity, cancellationToken);

    public async Task<Result<List<ObservableInstanceViewModel>>> GetAllInstancesAsync()
    {
        var result = await _facade.GetAllInstancesAsync();
        var error = result.WithoutData();
        var instances = result.Data;
        if (result.IsFailure())
        {
            return Result.FromFailure<List<ObservableInstanceViewModel>>(error);
        }

        var viewModels = (instances ?? [])
            .Select(MapToViewModel)
            .OrderByDescending(vm => vm.RegisteredAt)
            .ToList();

        logger.LogDebug("Retrieved {Count} observable instances", viewModels.Count);
        return Result.Success(viewModels);
    }

    public async Task<Result<ObservableInstanceViewModel?>> GetInstanceByIdAsync(string instanceId)
    {
        var result = await _facade.GetByIdAsync(instanceId);
        var error = result.WithoutData();
        var instance = result.Data;
        if (result.IsFailure())
        {
            return Result.FromFailure<ObservableInstanceViewModel?>(error);
        }

        return Result.Success(instance == null ? null : MapToViewModel(instance));
    }

    public async Task<Result<List<ObservableInstanceViewModel>>> GetInstancesByTypeAsync(Type type)
    {
        var result = await _facade.GetInstancesByTypeAsync(type);
        var error = result.WithoutData();
        var instances = result.Data;
        if (result.IsFailure())
        {
            return Result.FromFailure<List<ObservableInstanceViewModel>>(error);
        }

        var viewModels = (instances ?? [])
            .Select(MapToViewModel)
            .OrderByDescending(vm => vm.RegisteredAt)
            .ToList();

        logger.LogDebug("Retrieved {Count} instances of type {Type}", viewModels.Count, type.GetCleanName());
        return Result.Success(viewModels);
    }

    public async Task<Result<List<ObservableInstanceViewModel>>> GetInstancesByGroupAsync(string groupId)
    {
        var result = await _facade.GetInstancesByGroupAsync(groupId);
        var error = result.WithoutData();
        var instances = result.Data;
        if (result.IsFailure())
        {
            return Result.FromFailure<List<ObservableInstanceViewModel>>(error);
        }

        var viewModels = (instances ?? [])
            .Select(MapToViewModel)
            .OrderByDescending(vm => vm.RegisteredAt)
            .ToList();

        logger.LogDebug("Retrieved {Count} instances in group {GroupId}", viewModels.Count, groupId);
        return Result.Success(viewModels);
    }

    public async Task<Result<List<ObservableInstanceViewModel>>> GetInstancesWithExceptionsAsync()
    {
        var result = await _facade.GetInstancesWithExceptionsAsync();
        var error = result.WithoutData();
        var instances = result.Data;
        if (result.IsFailure())
        {
            return Result.FromFailure<List<ObservableInstanceViewModel>>(error);
        }

        var viewModels = (instances ?? [])
            .Select(MapToViewModel)
            .OrderByDescending(vm => vm.ExceptionCount)
            .ThenByDescending(vm => vm.RegisteredAt)
            .ToList();

        logger.LogDebug("Retrieved {Count} instances with exceptions", viewModels.Count);
        return Result.Success(viewModels);
    }

    public async Task<Result<List<ObservableInstanceViewModel>>> FilterInstancesAsync(ObservableInstanceFilter filter)
    {
        var result = await GetAllInstancesAsync();
        var error = result.WithoutData();
        var viewModels = result.Data;
        if (result.IsFailure())
        {
            return Result.FromFailure<List<ObservableInstanceViewModel>>(error);
        }

        var query = (viewModels ?? []).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var searchText = filter.SearchText.ToLowerInvariant();
            query = query.Where(vm =>
                vm.InstanceId.ToLowerInvariant().Contains(searchText) ||
                vm.InstanceName.ToLowerInvariant().Contains(searchText));
        }

        if (!string.IsNullOrWhiteSpace(filter.GroupId))
        {
            query = query.Where(vm => vm.GroupId == filter.GroupId);
        }

        if (filter.HealthStateFilter.HasValue)
        {
            query = query.Where(vm => vm.HealthState == filter.HealthStateFilter.Value);
        }

        if (filter.LogLevels?.Any() == true)
        {
            query = query.Where(vm =>
                vm.CurrentLogLevel.HasValue &&
                filter.LogLevels.Contains(vm.CurrentLogLevel.Value));
        }

        if (filter.ShowCriticalErrorOnly)
        {
            query = query.Where(vm =>
                vm.CurrentLogLevel == LogLevel.Critical ||
                vm.CurrentLogLevel == LogLevel.Error);
        }

        if (filter.ShowUnhealthyOnly)
        {
            query = query.Where(vm => vm.IsUnhealthy);
        }

        if (filter.RegisteredFrom.HasValue)
        {
            query = query.Where(vm => vm.RegisteredAt >= filter.RegisteredFrom.Value);
        }

        if (filter.RegisteredTo.HasValue)
        {
            query = query.Where(vm => vm.RegisteredAt <= filter.RegisteredTo.Value);
        }

        var filtered = query
            .OrderByDescending(vm => vm.RegisteredAt)
            .ToList();

        logger.LogDebug("Filtered observable instances: {Count} results", filtered.Count);
        return Result.Success(filtered);
    }

    public async Task<Result<ObservableInstanceStatistics>> GetStatisticsAsync()
    {
        var result = await _facade.GetAllInstancesAsync();
        var error = result.WithoutData();
        var instances = result.Data;
        if (result.IsFailure())
        {
            return Result.FromFailure<ObservableInstanceStatistics>(error);
        }

        var allInstances = (instances ?? []).ToList();
        var viewModels = allInstances.Select(MapToViewModel).ToList();

        var healthyCount = viewModels.Count(vm => vm.HealthState == HealthState.Healthy);
        var unhealthyCount = viewModels.Count(vm => vm.HealthState == HealthState.Unhealthy);
        var unknownCount = viewModels.Count(vm => vm.HealthState == HealthState.Unknown);

        var logLevelDistribution = viewModels
            .Where(vm => vm.CurrentLogLevel.HasValue)
            .GroupBy(vm => vm.CurrentLogLevel!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var statistics = new ObservableInstanceStatistics
        {
            TotalInstances = allInstances.Count,
            HealthyCount = healthyCount,
            UnhealthyCount = unhealthyCount,
            UnknownHealthCount = unknownCount,
            LogLevelDistribution = logLevelDistribution,
            TotalStateChanges = allInstances.Sum(i => i.TotalStateChanges),
            TotalExceptions = allInstances.Sum(i => i.TotalExceptions),
            AverageHistoryPerInstance = allInstances.Count > 0
                ? allInstances.Average(i => i.Count)
                : 0,
            TopTypesByCount = allInstances
                .Where(i => i.InstanceType != null)
                .GroupBy(i => i.InstanceType!)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new TypeCount
                {
                    TypeName = g.Key.GetCleanFullName(),
                    TypeShortName = g.Key.GetCleanName(),
                    Count = g.Count()
                })
                .ToList(),
            TopGroupsByCount = allInstances
                .Where(i => !string.IsNullOrEmpty(i.GroupId))
                .GroupBy(i => i.GroupId!)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new GroupCount
                {
                    GroupId = g.Key,
                    Count = g.Count()
                })
                .ToList()
        };

        logger.LogDebug(
            "Generated observable instance statistics: Total={Total}, Healthy={Healthy}, Unhealthy={Unhealthy}, Unknown={Unknown}",
            statistics.TotalInstances,
            statistics.HealthyCount,
            statistics.UnhealthyCount,
            statistics.UnknownHealthCount);

        return Result.Success(statistics);
    }

    public async Task<Result<List<ObservableStateHistoryViewModel>>> GetInstanceHistoryAsync(string instanceId, int? limit = null)
    {
        var result = await _facade.GetInstanceHistoryAsync(instanceId, limit);
        var error = result.WithoutData();
        var history = result.Data;
        if (result.IsFailure())
        {
            return Result.FromFailure<List<ObservableStateHistoryViewModel>>(error);
        }

        var viewModels = (history ?? [])
            .Select(MapToHistoryViewModel)
            .OrderByDescending(vm => vm.Timestamp)
            .ThenByDescending(vm => vm.Sequence)
            .ToList();

        return Result.Success(viewModels);
    }

    public async Task<Result<List<ObservableStateHistoryViewModel>>> GetInstanceExceptionsAsync(string instanceId)
    {
        var result = await _facade.GetInstanceExceptionsAsync(instanceId);
        var error = result.WithoutData();
        var exceptions = result.Data;
        if (result.IsFailure())
        {
            return Result.FromFailure<List<ObservableStateHistoryViewModel>>(error);
        }

        var viewModels = (exceptions ?? [])
            .Select(MapToHistoryViewModel)
            .OrderByDescending(vm => vm.Timestamp)
            .ThenByDescending(vm => vm.Sequence)
            .ToList();

        return Result.Success(viewModels);
    }

    public Task<Result> ClearInstanceHistoryAsync(string instanceId)
    {
        return _facade.ClearInstanceHistoryAsync(instanceId);
    }

    private ObservableInstanceViewModel MapToViewModel(ObservableInstanceTracker instance)
    {
        return new ObservableInstanceViewModel
        {
            InstanceId = instance.InstanceId,
            InstanceName = instance.InstanceName,
            InstanceType = instance.InstanceType,
            InstanceKey = instance.InstanceKey,
            GroupId = instance.GroupId,
            CurrentState = instance.CurrentState,
            StateChangedAt = instance.StateChangedAt,
            RegisteredAt = instance.RegisteredAt,
            TotalStateChanges = instance.TotalStateChanges,
            TotalExceptions = instance.TotalExceptions,
            HistoryCount = instance.Count,
            ExceptionCount = instance.ExceptionCount,
            HasExceptions = instance.HasExceptions,
            CurrentLogLevel = instance.CurrentLogLevel,
            IsUnhealthy = instance.IsUnhealthy()
        };
    }

    private ObservableStateHistoryViewModel MapToHistoryViewModel(ObservableStateEntry history)
    {
        return new ObservableStateHistoryViewModel
        {
            Sequence = history.Sequence,
            Timestamp = history.Timestamp,
            PreviousState = history.PreviousState,
            CurrentState = history.CurrentState,
            Message = history.Message,
            Exception = history.Exception,
            LogLevel = history.LogLevel
        };
    }
}
