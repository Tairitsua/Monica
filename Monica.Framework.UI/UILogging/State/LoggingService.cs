using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Framework.UI.UILogging.Models;
using Monica.Framework.UI.UILogging.Support;
using Monica.Modules;

namespace Monica.Framework.UI.UILogging.State;

/// <summary>
/// Host-owned facade for the log catalog and a separate legacy host buffer. Built-in reader/catalog pages
/// borrow their circuit-scoped LoggingWorkspaceSession; page activity never changes the legacy /current/export buffer.
/// </summary>
/// <remarks>
/// Existing Initialize/Start/Pause/Filter/Switch methods deliberately continue operating on the host's
/// legacy reader. The host container owns and disposes that reader. CreateSession returns caller-owned
/// independent readers; the caller owns their retirement. The built-in scoped workspace is one such owner.
/// </remarks>
public sealed class LoggingService(
    ScreenLogBuffer buffer,
    LogTailService logTailService,
    LogFileQueryService logFileQueryService,
    IOptions<ModuleLoggingUIOption> uiOptions,
    ILogger<LoggingService> logger) : IAsyncDisposable
{
    private readonly LogReadingSession _legacy = new(buffer, logTailService, logFileQueryService, uiOptions, logger);
    /// <summary>Composition-time UI options; this facade does not mutate them.</summary>
    public ModuleLoggingUIOption UIOption => uiOptions.Value;
    /// <summary>Source selected by the legacy host reader; independent page readers do not change it.</summary>
    public string CurrentLogFilePath => _legacy.CurrentLogFilePath;
    /// <summary>Inspection directory shared by the stateless catalog and file-download endpoints.</summary>
    public string LogDirectory => logFileQueryService.LogDirectory;
    /// <summary>Following state of the legacy host reader.</summary>
    public bool IsRunning => _legacy.IsRunning;
    /// <summary>Complete physical lines observed by the legacy host reader.</summary>
    public long TotalFileLineCount => _legacy.TotalFileLineCount;
    /// <summary>Source existence observed at the legacy reader's last successful read.</summary>
    public bool LogFileExists => _legacy.LogFileExists;

    /// <summary>Creates an independent reader whose cursor, filters, buffer and following work must be disposed by the caller.</summary>
    public LogReadingSession CreateSession() => new(new ScreenLogBuffer(uiOptions), logTailService.CreateReader(), logFileQueryService, uiOptions, logger);

    /// <summary>Replaces the legacy host reader window; built-in pages instead borrow their scoped workspace reader.</summary>
    public Task<Result<ScreenLogSnapshot>> InitializeAsync(int requestedLines, CancellationToken cancellationToken = default)
        => _legacy.InitializeAsync(requestedLines, cancellationToken);
    /// <summary>Starts legacy host following work, owned by the host container rather than a page.</summary>
    public Task<Result> StartAsync(CancellationToken cancellationToken = default) => _legacy.StartAsync(cancellationToken);
    /// <summary>Stops and drains the legacy host reader only.</summary>
    public Task<Result> PauseAsync() => _legacy.PauseAsync();
    /// <summary>Changes the legacy host reader filter only.</summary>
    public Task<Result<ScreenLogSnapshot>> UpdateFilterAsync(string? keyword, bool onlyCapture) => _legacy.UpdateFilterAsync(keyword, onlyCapture);
    /// <summary>Returns the legacy reader's immutable adopted snapshot.</summary>
    public ScreenLogSnapshot GetSnapshot() => _legacy.GetSnapshot();
    /// <summary>Loads earlier complete physical lines into the legacy host buffer.</summary>
    public Task<Result<int>> LoadMoreLinesAsync(int lineCount = 50, CancellationToken cancellationToken = default)
        => _legacy.LoadMoreLinesAsync(lineCount, cancellationToken);
    /// <summary>Changes the source of the legacy host reader only.</summary>
    public Task<Result<ScreenLogSnapshot>> SwitchToFileAsync(string relativePath, int initialLines, CancellationToken cancellationToken = default)
        => _legacy.SwitchToFileAsync(relativePath, initialLines, cancellationToken);
    /// <summary>Subscribes to the legacy host reader; caller owns and disposes its subscription.</summary>
    public LoggingSubscription Subscribe() => _legacy.Subscribe();

    /// <summary>
    /// Exports the separate legacy host buffer with its existing timestamp/retention framing. This endpoint
    /// never exports any page-owned reader; reader pages use their own frozen session export.
    /// </summary>
    public Task<Result<LogExportResult>> ExportBufferAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = Encoding.UTF8.GetBytes(buffer.ExportCurrent());
        return Task.FromResult(Result.Success(new LogExportResult($"temp-logs-{DateTimeOffset.Now:yyyyMMddHHmmss}.log", "text/plain", content)));
    }

    /// <summary>Lists ordinary files beneath the inspection directory.</summary>
    public Task<Result<IReadOnlyList<LogFileDescriptor>>> ListFilesAsync(CancellationToken cancellationToken = default)
        => logFileQueryService.ListFilesAsync(cancellationToken);
    /// <summary>Opens an allowed live file; the caller owns and must dispose the stream.</summary>
    public Task<Result<FileStream>> OpenFileAsync(string relativePath, CancellationToken cancellationToken = default)
        => logFileQueryService.OpenFileAsync(relativePath, cancellationToken);
    /// <summary>Retires the facade's legacy reader. Independently created sessions remain their caller's responsibility.</summary>
    public ValueTask DisposeAsync() => _legacy.DisposeAsync();
}
