namespace Monica.DataChannel.UIDataChannel.Models;

/// <summary>
/// Aggregated exception statistics for the data channel layer.
/// </summary>
public class DataChannelExceptionSummaryInfo
{
    /// <summary>Gets or sets the UTC start time of the aggregate read.</summary>
    public DateTime CapturedAt { get; set; }
    /// <summary>
    /// Total number of registered channels.
    /// </summary>
    public int TotalChannels { get; set; }
    
    /// <summary>
    /// Channels whose captured history retains exception records.
    /// </summary>
    public int ChannelsWithExceptions { get; set; }
    
    /// <summary>
    /// Sum of retained exception counts across the captured channels.
    /// </summary>
    public int TotalCurrentExceptions { get; set; }

    /// <summary>
    /// Accumulated number of exception events recorded historically.
    /// </summary>
    public int TotalHistoricalExceptions { get; set; }
    
    /// <summary>
    /// Detailed statistics per channel.
    /// </summary>
    public List<DataChannelExceptionSummaryItem> ChannelSummaries { get; set; } = new();
}

/// <summary>
/// Per-channel exception statistics.
/// </summary>
public class DataChannelExceptionSummaryItem
{
    /// <summary>Gets or sets the UTC time of this channel's tracker capture.</summary>
    public DateTime CapturedAt { get; set; }

    /// <summary>Gets or sets the exact pipeline group ID; null or empty indicates ungrouped.</summary>
    public string? GroupId { get; set; }
    /// <summary>
    /// Identifier of the DataChannel.
    /// </summary>
    public string ChannelId { get; set; } = string.Empty;
    
    /// <summary>
    /// Identifier of the associated pipeline.
    /// </summary>
    public string PipelineId { get; set; } = string.Empty;
    
    /// <summary>
    /// Retained exception count for the channel.
    /// </summary>
    public long CurrentExceptionCount { get; set; }

    /// <summary>
    /// Total exception count recorded.
    /// </summary>
    public long TotalExceptionCount { get; set; }
    
    /// <summary>
    /// Shared observable history capacity, including ordinary records and exceptions.
    /// </summary>
    public int MaxPoolSize { get; set; }
    
    /// <summary>
    /// Indicates whether the channel currently has exceptions.
    /// </summary>
    public bool HasExceptions { get; set; }
    
    /// <summary>
    /// Timestamp of the latest retained exception, or null after all exception evidence is cleared or trimmed.
    /// </summary>
    public DateTime? LatestException { get; set; }

    internal IReadOnlyList<(string Type, string Message)> RetainedSearchText { get; init; } = [];
}
