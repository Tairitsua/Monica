using Monica.Core.Extensions;
using Microsoft.Extensions.Logging;
using Monica.Profiling.TypeAllocation.Services;
using Monica.Core.Results;
using Monica.Profiling.TypeAllocation.Models;

namespace Monica.Profiling.TypeAllocation.Facades;

/// <summary>
/// Provides result-envelope entry points for type allocation tracking and heap snapshots.
/// </summary>
/// <remarks>
/// Register <see cref="Monica.Modules.ModuleTypeAllocation" /> before resolving this scoped facade.
/// It borrows host-owned singleton providers: start, stop and reset affect the current process and
/// every consumer of that collector. Retiring a page does not stop or dispose those shared providers.
/// Live allocation traffic and heap enumeration are separate sources; a heap result must be retained
/// by its caller rather than read back through the live snapshot methods. Check each result envelope
/// before consuming its data; registration alone does not establish native capability or permission.
/// </remarks>
public sealed class TypeAllocationFacade
{
    private readonly TypeAllocationTrackingService _typeAllocationTrackingService;
    private readonly ILogger<TypeAllocationFacade> _logger;

    internal TypeAllocationFacade(
        TypeAllocationTrackingService typeAllocationTrackingService,
        ILogger<TypeAllocationFacade> logger)
    {
        _typeAllocationTrackingService = typeAllocationTrackingService;
        _logger = logger;
    }

    /// <summary>Gets whether the shared live collector currently admits allocation events.</summary>
    public bool IsCollecting => _typeAllocationTrackingService.IsCollecting;

    /// <summary>Gets the active session's legacy mode, or Disabled when no session is active.</summary>
    /// <remarks>A stopped snapshot retains its original SamplingMode independently of this current state.</remarks>
    public AllocationSamplingMode CurrentMode => _typeAllocationTrackingService.CurrentMode;

    /// <summary>Reads detached current or retained stopped live allocation evidence without starting collection.</summary>
    /// <remarks>
    /// This method never returns a heap census. Before collection the source is None; active evidence
    /// uses its UTC read time and stopped evidence retains its stop timestamp, mode and duration.
    /// Totals survive stop/start until reset, while duration describes only the current/last session
    /// window or the window since its reset. Zero retained type rows can represent totals-only capture.
    /// </remarks>
    /// <returns>The live snapshot, or a failed envelope when the provider read fails.</returns>
    public Result<TypeAllocationSnapshot> GetCurrentSnapshot()
    {
        try
        {
            return Result.Success(_typeAllocationTrackingService.GetCurrentSnapshot());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the current type allocation snapshot.");
            return Result.Failure<TypeAllocationSnapshot>($"Failed to load type allocation snapshot: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>Reads detached snapshots from the shared live collector's bounded history, oldest first.</summary>
    /// <remarks>
    /// History survives stop/start until reset and excludes separately captured heap results.
    /// Every read owns its snapshot/type-row copies; modifying them cannot alter retained history.
    /// </remarks>
    /// <returns>The retained live history, which may be empty, or a failed provider-read envelope.</returns>
    public Result<IReadOnlyList<TypeAllocationSnapshot>> GetHistorySnapshots()
    {
        try
        {
            return Result.Success(_typeAllocationTrackingService.GetHistorySnapshots());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load type allocation history.");
            return Result.Failure<IReadOnlyList<TypeAllocationSnapshot>>($"Failed to load type allocation history: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>Explicitly starts current-process Windows ETW allocation-tick collection for all consumers.</summary>
    /// <remarks>
    /// Requires an identity permitted to create the trace session and enable the CLR provider; no
    /// EventPipe fallback is implemented. Cumulative counters/history survive until reset. The host's
    /// AutoStopAfter option applies to this session, including manually started collection.
    /// A running session, unsupported mode/platform, permission failure or synchronous callback
    /// lifecycle re-entry produces a failed envelope rather than starting another collector.
    /// </remarks>
    /// <param name="mode">Low by default. Low and High use the same tick stream, without distinct sampling-rate guarantees.</param>
    /// <returns>The actual shared start outcome.</returns>
    public Result StartCollection(AllocationSamplingMode mode = AllocationSamplingMode.Low)
    {
        try
        {
            _typeAllocationTrackingService.StartCollection(mode);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start type allocation collection.");
            return Result.Failure(ex.GetMessageRecursively());
        }
    }

    /// <summary>Explicitly stops the shared live collector and drains its admitted processing/publication.</summary>
    /// <remarks>
    /// The synchronous operation closes the native session and waits for event, snapshot and expiry
    /// work. Final evidence retains cumulative totals, stop timestamp, recorded mode and window
    /// duration. Calling this with no active session or synchronously from a notification fails.
    /// Page retirement alone must not invoke this host-wide action.
    /// </remarks>
    /// <returns>The actual shared stop outcome.</returns>
    public Result StopCollection()
    {
        try
        {
            _typeAllocationTrackingService.StopCollection();
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop type allocation collection.");
            return Result.Failure(ex.GetMessageRecursively());
        }
    }

    /// <summary>Clears shared live type rows, cumulative counters, history and prior collection errors.</summary>
    /// <remarks>
    /// A running collector continues with a new counter/duration window. Reset does not stop it,
    /// force GC or invalidate a caller's separately retained heap result. Synchronous lifecycle
    /// re-entry from a notification returns failure. The UI can impose stricter inactive-only admission.
    /// </remarks>
    /// <returns>The actual shared reset outcome.</returns>
    public Result ResetData()
    {
        try
        {
            _typeAllocationTrackingService.ResetData();
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset type allocation data.");
            return Result.Failure(ex.GetMessageRecursively());
        }
    }

    /// <summary>Subscribes to shared live evidence and returns the action that removes this subscription.</summary>
    /// <remarks>
    /// Notifications run synchronously on the publisher, which can be a provider worker or a command
    /// caller. Each subscriber receives detached rows; subscriber exceptions are logged independently.
    /// Marshal UI work and invoke the returned unsubscribe action when the consumer retires. A callback
    /// already admitted before unsubscription may still complete, so guard late publication locally.
    /// Schedule start/stop/reset after the callback returns; synchronous lifecycle re-entry is rejected.
    /// Removing a subscription neither stops collection nor disposes the borrowed provider.
    /// </remarks>
    /// <param name="callback">The consumer of live snapshot updates.</param>
    /// <returns>The subscription's removal action.</returns>
    public Action SubscribeToUpdates(Action<TypeAllocationSnapshot> callback)
    {
        return _typeAllocationTrackingService.SubscribeToUpdates(callback);
    }

    /// <summary>Captures a Windows PSS clone of the current process and enumerates its valid non-free heap objects.</summary>
    /// <remarks>
    /// Requires Windows 8.1/Server 2012 R2 or later, a managed runtime, a matching readable DAC and
    /// a walkable heap. The host-owned provider serializes admitted scans and disposes the CLR runtime
    /// before its target. This operation has no cancellation token or timeout; consumer retirement
    /// cannot forcibly cancel native capture/walking, and host disposal drains admitted scans.
    /// It performs no forced GC, dump creation, passive live attach or non-Windows dump fallback.
    /// Enumeration does not prove root reachability or validate the complete heap; unreadable/corrupt
    /// segment metadata can limit the walk. Counts/bytes describe the objects actually enumerated,
    /// and the timestamp describes enumeration completion rather than native clone creation.
    /// A successful heap result is independent of live tracking and is retained by its caller.
    /// </remarks>
    /// <returns>The enumerated heap result, or a failed envelope preserving a native/platform/DAC failure.</returns>
    public async Task<Result<TypeAllocationSnapshot>> TakeHeapSnapshotAsync()
    {
        try
        {
            return Result.Success(await _typeAllocationTrackingService.TakeHeapSnapshotAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture heap snapshot.");
            return Result.Failure<TypeAllocationSnapshot>($"Failed to capture heap snapshot: {ex.GetMessageRecursively()}");
        }
    }
}
