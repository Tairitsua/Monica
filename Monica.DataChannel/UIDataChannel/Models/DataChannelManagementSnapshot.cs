using Monica.DataChannel.Models;

namespace Monica.DataChannel.UIDataChannel.Models;

/// <summary>
/// Detached management evidence for one host. Each channel's status and exception summary share
/// one tracker capture; channels and component values are independent reads, not an atomic host snapshot.
/// </summary>
public sealed class DataChannelManagementSnapshot
{
    /// <summary>Gets the UTC start time of this management read.</summary>
    public DateTime CapturedAt { get; init; }
    /// <summary>Gets setup lifecycle observations, including unconstructed held setups.</summary>
    public IReadOnlyList<DataChannelSetupSnapshot> Setups { get; init; } = [];
    /// <summary>Gets registered channels in manager order.</summary>
    public List<DataChannelStatusInfo> Channels { get; init; } = [];
    /// <summary>Gets exception statistics derived from the same channel captures.</summary>
    public DataChannelExceptionSummaryInfo ExceptionSummary { get; init; } = new();
}
