using Microsoft.Extensions.Logging;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Core.ObservableInstance.Models;
using Monica.Core.Results;

namespace Monica.Core.ObservableInstance.Facades;

/// <summary>
/// Facade wrapper for observable instance queries and maintenance operations.
/// </summary>
public sealed class ObservableInstanceFacade(
    IObservableInstanceRegistry observableInstanceRegistry,
    ILogger<ObservableInstanceFacade> logger)
{
    private const string InstanceNotFoundMessage = "Observable instance was not found.";
    private readonly IObservableInstanceRegistry _registry = observableInstanceRegistry;

    /// <summary>
    /// Reads the current registry membership once and captures every tracker independently.
    /// A concurrent unregister is omitted; this is not a globally atomic multi-tracker transaction.
    /// </summary>
    /// <param name="cancellationToken">Cancels before the read and between tracker captures.</param>
    /// <returns>Detached instance evidence, or a read failure.</returns>
    public Task<Result<IReadOnlyList<ObservableInstanceSnapshot>>> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var snapshots = new List<ObservableInstanceSnapshot>();
            foreach (var tracker in _registry.GetAllInstances())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { snapshots.Add(tracker.CaptureSnapshot(cancellationToken)); }
                catch (ObjectDisposedException) { /* A registration was retired after the membership read. */ }
            }
            return Task.FromResult<Result<IReadOnlyList<ObservableInstanceSnapshot>>>(Result.Success<IReadOnlyList<ObservableInstanceSnapshot>>(snapshots.AsReadOnly()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to capture observable instances.");
            return Task.FromResult<Result<IReadOnlyList<ObservableInstanceSnapshot>>>(Result.Failure<IReadOnlyList<ObservableInstanceSnapshot>>(exception.Message));
        }
    }

    /// <summary>Clears retained history only when the original registration still owns the ID.</summary>
    /// <param name="identity">The registration identity captured by the caller.</param>
    /// <param name="cancellationToken">Cancels before the clear operation.</param>
    /// <returns>A failure for a missing, retired, or replacement registration.</returns>
    public Task<Result> ClearInstanceHistoryAsync(ObservableInstanceReference identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var tracker = _registry.GetById(identity.InstanceId);
            if (tracker is null || tracker.RegistrationId != identity.RegistrationId)
                return Task.FromResult<Result>(Result.Failure("The captured registration is no longer available. Refresh before clearing history."));
            cancellationToken.ThrowIfCancellationRequested();
            tracker.ClearHistory();
            return Task.FromResult<Result>(Result.Success());
        }
        catch (ObjectDisposedException) { return Task.FromResult<Result>(Result.Failure("The captured registration was retired.")); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to clear retained history for captured registration {InstanceId}.", identity.InstanceId);
            return Task.FromResult<Result>(Result.Failure(exception.Message));
        }
    }

    public Task<Result<IReadOnlyList<ObservableInstanceTracker>>> GetAllInstancesAsync()
    {
        try
        {
            return Task.FromResult<Result<IReadOnlyList<ObservableInstanceTracker>>>(Result.Success(_registry.GetAllInstances()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get all observable instances.");
            return Task.FromResult<Result<IReadOnlyList<ObservableInstanceTracker>>>(Result.Failure<IReadOnlyList<ObservableInstanceTracker>>($"Failed to get observable instances: {ex.Message}"));
        }
    }

    public Task<Result<ObservableInstanceTracker?>> GetByIdAsync(string instanceId)
    {
        try
        {
            return Task.FromResult<Result<ObservableInstanceTracker?>>(Result.Success(_registry.GetById(instanceId)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get observable instance {InstanceId}.", instanceId);
            return Task.FromResult<Result<ObservableInstanceTracker?>>(Result.Failure<ObservableInstanceTracker?>($"Failed to get observable instance details: {ex.Message}"));
        }
    }

    public Task<Result<IReadOnlyList<ObservableInstanceTracker>>> GetInstancesByTypeAsync(Type type)
    {
        try
        {
            return Task.FromResult<Result<IReadOnlyList<ObservableInstanceTracker>>>(Result.Success(_registry.GetInstancesByType(type)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get observable instances by type {Type}.", type);
            return Task.FromResult<Result<IReadOnlyList<ObservableInstanceTracker>>>(Result.Failure<IReadOnlyList<ObservableInstanceTracker>>($"Failed to get observable instances by type: {ex.Message}"));
        }
    }

    public Task<Result<IReadOnlyList<ObservableInstanceTracker>>> GetInstancesByGroupAsync(string groupId)
    {
        try
        {
            return Task.FromResult<Result<IReadOnlyList<ObservableInstanceTracker>>>(Result.Success(_registry.GetInstancesByGroup(groupId)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get observable instances by group {GroupId}.", groupId);
            return Task.FromResult<Result<IReadOnlyList<ObservableInstanceTracker>>>(Result.Failure<IReadOnlyList<ObservableInstanceTracker>>($"Failed to get observable instances by group: {ex.Message}"));
        }
    }

    public Task<Result<IReadOnlyList<ObservableInstanceTracker>>> GetInstancesWithExceptionsAsync()
    {
        try
        {
            return Task.FromResult<Result<IReadOnlyList<ObservableInstanceTracker>>>(Result.Success(_registry.GetInstancesWithExceptions()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get observable instances with exceptions.");
            return Task.FromResult<Result<IReadOnlyList<ObservableInstanceTracker>>>(Result.Failure<IReadOnlyList<ObservableInstanceTracker>>($"Failed to get observable instances with exceptions: {ex.Message}"));
        }
    }

    public Task<Result<IReadOnlyList<ObservableStateEntry>>> GetInstanceHistoryAsync(string instanceId, int? limit = null)
    {
        try
        {
            var tracker = _registry.GetById(instanceId);
            if (tracker == null)
            {
                return Task.FromResult<Result<IReadOnlyList<ObservableStateEntry>>>(Result.Failure<IReadOnlyList<ObservableStateEntry>>(InstanceNotFoundMessage));
            }

            IReadOnlyList<ObservableStateEntry> history = limit.HasValue && limit.Value > 0
                ? tracker.GetRecentHistory(limit.Value)
                : tracker.GetHistory();

            return Task.FromResult<Result<IReadOnlyList<ObservableStateEntry>>>(Result.Success(history));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get observable instance history for {InstanceId}.", instanceId);
            return Task.FromResult<Result<IReadOnlyList<ObservableStateEntry>>>(Result.Failure<IReadOnlyList<ObservableStateEntry>>($"Failed to get observable instance history: {ex.Message}"));
        }
    }

    public Task<Result<IReadOnlyList<ObservableStateEntry>>> GetInstanceExceptionsAsync(string instanceId)
    {
        try
        {
            var tracker = _registry.GetById(instanceId);
            if (tracker == null)
            {
                return Task.FromResult<Result<IReadOnlyList<ObservableStateEntry>>>(Result.Failure<IReadOnlyList<ObservableStateEntry>>(InstanceNotFoundMessage));
            }

            return Task.FromResult<Result<IReadOnlyList<ObservableStateEntry>>>(Result.Success(tracker.GetExceptions()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get observable instance exceptions for {InstanceId}.", instanceId);
            return Task.FromResult<Result<IReadOnlyList<ObservableStateEntry>>>(Result.Failure<IReadOnlyList<ObservableStateEntry>>($"Failed to get observable instance exceptions: {ex.Message}"));
        }
    }

    public Task<Result> ClearInstanceHistoryAsync(string instanceId)
    {
        try
        {
            var tracker = _registry.GetById(instanceId);
            if (tracker == null)
            {
                return Task.FromResult<Result>(Result.Failure(InstanceNotFoundMessage));
            }

            tracker.ClearHistory();
            return Task.FromResult<Result>(Result.Success().WithMessage("Observable instance history was cleared."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear observable instance history for {InstanceId}.", instanceId);
            return Task.FromResult<Result>(Result.Failure($"Failed to clear observable instance history: {ex.Message}"));
        }
    }
}
