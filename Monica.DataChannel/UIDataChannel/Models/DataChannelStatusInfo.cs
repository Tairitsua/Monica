namespace Monica.DataChannel.UIDataChannel.Models;

/// <summary>
/// Status snapshot of a DataChannel for the UI layer.
/// </summary>
public class DataChannelStatusInfo
{
    /// <summary>
    /// Channel ID
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the exact pipeline group ID; null or empty indicates ungrouped.</summary>
    public string? GroupId { get; set; }

    /// <summary>Gets or sets the UTC time of this channel's tracker capture.</summary>
    public DateTime CapturedAt { get; set; }

    /// <summary>Gets or sets shared observable retention capacity, including ordinary records.</summary>
    public int MaxHistorySize { get; set; }
    
    /// <summary>
    /// Detached captures of the configured middleware registrations.
    /// </summary>
    public List<PipelineComponentInfo> Middlewares { get; set; } = new();
    
    /// <summary>
    /// Representation of the pipeline's inner endpoint.
    /// </summary>
    public PipelineComponentInfo InnerEndpoint { get; set; } = null!;
    
    /// <summary>
    /// Representation of the pipeline's outer endpoint.
    /// </summary>
    public PipelineComponentInfo OuterEndpoint { get; set; } = null!;
    
    /// <summary>
    /// Flag indicating whether the channel is currently unavailable.
    /// </summary>
    public bool IsNotAvailable { get; set; }
    
    /// <summary>
    /// Indicates whether the channel has finished initialization.
    /// </summary>
    public bool IsInitialized { get; set; }
    
    /// <summary>
    /// Indicates whether the channel is currently initializing.
    /// </summary>
    public bool IsInitializing { get; set; }

    /// <summary>Gets or sets the latest failed local initialization message, independent of retained history.</summary>
    public string? InitializationError { get; set; }
    
    /// <summary>
    /// Indicates whether the captured history retains any exception records.
    /// </summary>
    public bool HasExceptions { get; set; }
    
    /// <summary>
    /// Number of retained exception records; this is not an active failure count.
    /// </summary>
    public int ExceptionCount { get; set; }
    
    /// <summary>
    /// Cumulative exception count for the channel.
    /// </summary>
    public int TotalExceptionCount { get; set; }
}
