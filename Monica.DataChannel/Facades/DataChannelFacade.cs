using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Core.ObservableInstance.Models;
using Monica.Core.Results;
using Monica.Core;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Models;
using Monica.DataChannel.Pipeline;
using Monica.DataChannel.UIDataChannel.Models;

namespace Monica.DataChannel.Facades;

/// <summary>Provides host-facing detached management evidence and real DataChannel operations.</summary>
/// <param name="manager">Manager exposing channels owned by this host.</param>
/// <param name="logger">Host-owned logger for management failures.</param>
/// <param name="application">The host's frozen participation policy, when composed through Monica.</param>
public class DataChannelFacade(IDataChannelManager manager, ILogger<DataChannelFacade> logger, MonicaApplication? application = null)
{
    /// <summary>
    /// Captures directory and exception summary from one tracker read per channel. Component values and
    /// different channels are independent reads; this does not claim atomic host-wide consistency.
    /// </summary>
    /// <param name="cancellationToken">Cancels before and between channel reads.</param>
    /// <returns>Complete detached evidence, or a failed result without partial data.</returns>
    public Task<Res<DataChannelManagementSnapshot>> GetManagementSnapshotAsync(CancellationToken cancellationToken = default) =>
        Capture(() =>
        {
            var capturedAt = DateTime.UtcNow;
            var statuses = new List<DataChannelStatusInfo>();
            var summaries = new List<DataChannelExceptionSummaryItem>();
            foreach (var channel in manager.FetchAll())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tracker = channel.Pipe.ObservableTracker.CaptureSnapshot(cancellationToken);
                statuses.Add(CaptureStatus(channel, tracker));
                summaries.Add(CaptureExceptionSummary(channel, tracker));
            }
            return new DataChannelManagementSnapshot
            {
                CapturedAt = capturedAt,
                Channels = statuses,
                ExceptionSummary = BuildExceptionSummary(capturedAt, summaries)
            };
        }, "retrieve DataChannel management evidence", cancellationToken);

    /// <summary>Captures registered channel identity, component values, initialization state and exception counts.</summary>
    /// <param name="cancellationToken">Cancels before and between channel reads.</param>
    /// <returns>Detached channel captures in manager order.</returns>
    public Task<Res<List<DataChannelStatusInfo>>> GetChannelsStatusAsync(CancellationToken cancellationToken = default) =>
        Capture(() => manager.FetchAll().Select(channel =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CaptureStatus(channel, channel.Pipe.ObservableTracker.CaptureSnapshot(cancellationToken));
        }).ToList(), "retrieve DataChannel status information", cancellationToken);

    /// <summary>Runs real endpoint initialization; concurrent attempts fail explicitly and cancellation propagates.</summary>
    /// <param name="id">Exact channel identifier.</param>
    /// <param name="cancellationToken">Cancels the admitted endpoint operation.</param>
    /// <returns>The completed operation result, never early success for another active attempt.</returns>
    public async Task<Res> ReInitializeChannelAsync(string id, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (application?.Application.AutonomousWork == false)
                return Res.Fail("Data channels are held while Application:AutonomousWork is false.");
            var channel = manager.Fetch(id);
            if (channel is null) return Res.Fail($"DataChannel '{id}' was not found.");
            await channel.ReInitialize(cancellationToken);
            return Res.Ok("DataChannel reinitialized successfully.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to reinitialize DataChannel {DataChannelId}.", id);
            return Res.Fail($"Failed to reinitialize DataChannel '{id}': {exception.GetMessageRecursively()}");
        }
    }

    /// <summary>Captures a recent exception subset with retained and lifetime counts from the same tracker read.</summary>
    /// <param name="id">Exact channel identifier.</param>
    /// <param name="count">Maximum details to return; nonpositive values return an empty subset.</param>
    /// <param name="cancellationToken">Cancels the tracker capture.</param>
    /// <returns>Detached exception evidence; source provenance is empty because the tracker does not record it.</returns>
    public Task<Res<DataChannelExceptionInfo>> GetChannelExceptionsAsync(string id, int count = 10,
        CancellationToken cancellationToken = default) => Capture(() =>
        {
            var channel = RequireChannel(id);
            var tracker = channel.Pipe.ObservableTracker.CaptureSnapshot(cancellationToken);
            var retained = tracker.History.Where(entry => entry.Exception is not null).ToList();
            return new DataChannelExceptionInfo
            {
                ChannelId = channel.Id,
                PipelineId = tracker.Identity.InstanceId,
                CapturedAt = tracker.CapturedAt,
                RequestedCount = count,
                CurrentExceptions = retained.Count,
                TotalExceptions = tracker.TotalExceptions,
                MaxPoolSize = tracker.MaxHistorySize,
                HasExceptions = retained.Count > 0,
                Exceptions = retained.OrderByDescending(entry => entry.Timestamp).ThenByDescending(entry => entry.Sequence)
                    .Take(Math.Max(0, count)).Select(entry => new ExceptionDetailInfo
                    {
                        Sequence = entry.Sequence,
                        Timestamp = entry.Timestamp,
                        Description = entry.Message,
                        SourceType = string.Empty,
                        SourceDescription = string.Empty,
                        ExceptionType = entry.Exception!.TypeName,
                        Message = entry.Exception.Message,
                        StackTrace = entry.Exception.Details,
                        CaptureError = entry.Exception.CaptureError
                    }).ToList()
            };
        }, $"retrieve exception information for DataChannel '{id}'", cancellationToken);

    /// <summary>Builds exception aggregates from each channel's internally consistent tracker capture.</summary>
    /// <param name="cancellationToken">Cancels before and between channel reads.</param>
    /// <returns>Retained and lifetime exception statistics, including channel capture times.</returns>
    public Task<Res<DataChannelExceptionSummaryInfo>> GetExceptionSummaryAsync(CancellationToken cancellationToken = default) =>
        Capture(() =>
        {
            var capturedAt = DateTime.UtcNow;
            var items = manager.FetchAll().Select(channel =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CaptureExceptionSummary(channel, channel.Pipe.ObservableTracker.CaptureSnapshot(cancellationToken));
            }).ToList();
            return BuildExceptionSummary(capturedAt, items);
        }, "retrieve the DataChannel exception summary", cancellationToken);

    /// <summary>
    /// Clears all retained observable records for this channel, including ordinary records and exceptions.
    /// Lifetime counters and endpoint initialization state are preserved; this does not resolve a failure.
    /// </summary>
    /// <param name="id">Exact channel identifier.</param>
    /// <returns>The history-clear operation result.</returns>
    public Task<Res> ClearChannelExceptionsAsync(string id)
    {
        try
        {
            RequireChannel(id).Pipe.ObservableTracker.ClearHistory();
            return Task.FromResult(Res.Ok("DataChannel retained observable history cleared successfully."));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to clear retained history for DataChannel {DataChannelId}.", id);
            return Task.FromResult(Res.Fail($"Failed to clear retained history for DataChannel '{id}': {exception.GetMessageRecursively()}"));
        }
    }

    /// <summary>Resolves an exact live host-owned middleware using an opaque channel-local registration key.</summary>
    /// <typeparam name="T">The required middleware contract.</typeparam>
    /// <param name="channelId">Exact channel identifier.</param>
    /// <param name="componentKey">Owner-issued key from a component capture.</param>
    /// <returns>The original live middleware, or failure for missing, removed, replaced or transient registrations.</returns>
    public Task<Res<T?>> GetMiddlewareByKeyAsync<T>(string channelId, string componentKey) where T : class =>
        Capture<T?>(() =>
        {
            var middleware = RequireChannel(channelId).Pipe.ResolveMiddleware(componentKey);
            return middleware is not TransientComponentProxyBase && middleware is T value ? value :
                throw new InvalidOperationException($"Middleware registration '{componentKey}' is unavailable in DataChannel '{channelId}'.");
        }, $"retrieve middleware registration '{componentKey}' from DataChannel '{channelId}'");

    /// <summary>Resolves a persistent middleware by declared CLR short name, rejecting ambiguous matches.</summary>
    /// <typeparam name="T">The required middleware contract.</typeparam>
    /// <param name="channelId">Exact channel identifier.</param>
    /// <param name="middlewareName">Declared CLR short name; use a component key for repeated names.</param>
    /// <returns>The unique live middleware, or a failed result when missing, ambiguous or transient.</returns>
    public Task<Res<T?>> GetMiddlewareAsync<T>(string channelId, string middlewareName) where T : class =>
        Capture<T?>(() =>
        {
            var matches = RequireChannel(channelId).Pipe.GetMiddlewares().Where(component =>
                (component is TransientComponentProxyBase proxy ? proxy.DeclaredComponentType : component.GetType()).Name == middlewareName).ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException($"Middleware name '{middlewareName}' is ambiguous in DataChannel '{channelId}'; use its component key.");
            return matches.SingleOrDefault() is T value && value is not TransientComponentProxyBase ? value :
                throw new InvalidOperationException($"Middleware '{middlewareName}' was not found in DataChannel '{channelId}'.");
        }, $"retrieve middleware '{middlewareName}' from DataChannel '{channelId}'");

    private DataChannel RequireChannel(string id) => manager.Fetch(id)
        ?? throw new InvalidOperationException($"DataChannel '{id}' was not found.");

    private static DataChannelStatusInfo CaptureStatus(DataChannel channel, ObservableInstanceSnapshot tracker)
    {
        var components = channel.Pipe.GetComponentRegistrations().Select(registration => new PipelineComponentInfo(registration)).ToList();
        var initialization = channel.Pipe.CaptureInitializationState();
        var retainedExceptions = tracker.History.Count(entry => entry.Exception is not null);
        return new DataChannelStatusInfo
        {
            Id = channel.Id,
            GroupId = channel.Pipe.GroupId,
            CapturedAt = tracker.CapturedAt,
            MaxHistorySize = tracker.MaxHistorySize,
            Middlewares = components.Where(component => component.Stage is PipelineComponentStage.EndpointConfiguration or PipelineComponentStage.Transform).ToList(),
            InnerEndpoint = components.Single(component => component.Stage == PipelineComponentStage.InnerEndpoint),
            OuterEndpoint = components.Single(component => component.Stage == PipelineComponentStage.OuterEndpoint),
            IsInitialized = initialization.IsInitialized,
            IsInitializing = initialization.IsInitializing,
            IsNotAvailable = initialization.IsNotAvailable,
            HasExceptions = retainedExceptions > 0,
            ExceptionCount = retainedExceptions,
            TotalExceptionCount = tracker.TotalExceptions
        };
    }

    private static DataChannelExceptionSummaryItem CaptureExceptionSummary(DataChannel channel, ObservableInstanceSnapshot tracker)
    {
        var exceptions = tracker.History.Where(entry => entry.Exception is not null).ToList();
        return new DataChannelExceptionSummaryItem
        {
            ChannelId = channel.Id,
            GroupId = channel.Pipe.GroupId,
            PipelineId = tracker.Identity.InstanceId,
            CapturedAt = tracker.CapturedAt,
            CurrentExceptionCount = exceptions.Count,
            TotalExceptionCount = tracker.TotalExceptions,
            MaxPoolSize = tracker.MaxHistorySize,
            HasExceptions = exceptions.Count > 0,
            LatestException = exceptions.Count > 0 ? exceptions.Max(entry => entry.Timestamp) : null
        };
    }

    private static DataChannelExceptionSummaryInfo BuildExceptionSummary(DateTime capturedAt, List<DataChannelExceptionSummaryItem> items) => new()
    {
        CapturedAt = capturedAt,
        TotalChannels = items.Count,
        ChannelsWithExceptions = items.Count(item => item.HasExceptions),
        TotalCurrentExceptions = checked((int)items.Sum(item => item.CurrentExceptionCount)),
        TotalHistoricalExceptions = checked((int)items.Sum(item => item.TotalExceptionCount)),
        ChannelSummaries = items
    };

    private Task<Res<T>> Capture<T>(Func<T> capture, string operation, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = capture();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Res.Ok(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to {Operation}.", operation);
            return Task.FromResult<Res<T>>(Res.Fail($"Failed to {operation}: {exception.GetMessageRecursively()}"));
        }
    }
}
