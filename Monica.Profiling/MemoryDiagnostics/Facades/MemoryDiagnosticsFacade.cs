using Monica.Core.Extensions;
using Microsoft.Extensions.Logging;
using Monica.Profiling.MemoryDiagnostics.Services;
using Monica.Core.Results;
using Monica.Profiling.MemoryDiagnostics.Models;

namespace Monica.Profiling.MemoryDiagnostics.Facades;

/// <summary>
/// Provides result-envelope entry points for memory snapshots, GC details, and dump creation.
/// </summary>
public sealed class MemoryDiagnosticsFacade
{
    private readonly MemoryDiagnosticsService _memoryDiagnosticsService;
    private readonly ILogger<MemoryDiagnosticsFacade> _logger;
    private readonly Func<string?, Task<string>> _createGcDump;

    internal MemoryDiagnosticsFacade(
        MemoryDiagnosticsService memoryDiagnosticsService,
        ILogger<MemoryDiagnosticsFacade> logger)
    {
        _memoryDiagnosticsService = memoryDiagnosticsService;
        _logger = logger;
        _createGcDump = memoryDiagnosticsService.CreateGcDumpAsync;
    }

    // A creator boundary permits result-envelope verification without launching a diagnostic process.
    internal MemoryDiagnosticsFacade(
        MemoryDiagnosticsService memoryDiagnosticsService,
        ILogger<MemoryDiagnosticsFacade> logger,
        Func<string?, Task<string>> createGcDump)
        : this(memoryDiagnosticsService, logger)
    {
        ArgumentNullException.ThrowIfNull(createGcDump);
        _createGcDump = createGcDump;
    }

    public Result<MemorySnapshot> GetCurrentSnapshot()
    {
        try
        {
            return Result.Success(_memoryDiagnosticsService.CaptureSnapshot());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture the current memory snapshot.");
            return Result.Failure<MemorySnapshot>($"Failed to capture memory snapshot: {ex.GetMessageRecursively()}");
        }
    }

    public Result<GcDetails> GetGcDetails(GCKind kind = GCKind.Any)
    {
        try
        {
            return Result.Success(_memoryDiagnosticsService.CaptureGcDetails(kind));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture GC details.");
            return Result.Failure<GcDetails>($"Failed to capture GC details: {ex.GetMessageRecursively()}");
        }
    }

    public Result ForceGarbageCollection(int generation = -1, bool blocking = true, bool compacting = false)
    {
        try
        {
            _memoryDiagnosticsService.ForceGarbageCollection(generation, blocking, compacting);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to force garbage collection.");
            return Result.Failure($"GC execution failed: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>Requests a GC dump and returns its actual server output path as result data.</summary>
    /// <remarks>
    /// The host must provide dotnet-gcdump on its process PATH and permit diagnostics/output access.
    /// The tool requests a full GC in the current host. This facade adds no cancellation token or timeout;
    /// the tool retains its own collection-timeout policy. The returned absolute server file path is not a
    /// browser download URL; callers own retention and transfer. Success requires exit code zero and a
    /// nonempty output file, without proving that the tool captured a complete heap. Provider failures remain
    /// failed envelopes.
    /// </remarks>
    /// <param name="outputPath">
    /// The server output path, or null for a collision-resistant temporary-directory default. Relative paths
    /// resolve against the host working directory; .gcdump is appended unless the extension is exactly .gcdump.
    /// </param>
    /// <returns>The verified absolute server path in Data on success, or a failed envelope without path data.</returns>
    public async Task<Result<string>> TriggerGcDumpAsync(string? outputPath = null)
    {
        try
        {
            return Result.Success<string>(await _createGcDump(outputPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create GC dump.");
            return Result.Failure<string>($"Failed to create GC dump: {ex.GetMessageRecursively()}", ResultStatus.BadRequest);
        }
    }
}
