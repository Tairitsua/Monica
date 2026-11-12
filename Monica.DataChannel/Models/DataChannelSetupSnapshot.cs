using Monica.Core.TypeDiscovery.Abstractions;

namespace Monica.DataChannel.Models;

/// <summary>The lifecycle of one host-owned setup, independently of its activation policy.</summary>
public enum DataChannelSetupState
{
    /// <summary>The setup has not been constructed or executed.</summary>
    Held,
    /// <summary>The declaring owner is not eligible in this host.</summary>
    Disabled,
    /// <summary>The setup's endpoints are stopped.</summary>
    Stopped,
    /// <summary>An explicit or automatic startup is in progress.</summary>
    Starting,
    /// <summary>All declared channels have completed initialization.</summary>
    Running,
    /// <summary>Endpoint cleanup is in progress.</summary>
    Stopping,
    /// <summary>The last lifecycle operation failed.</summary>
    Failed
}

/// <summary>Detached setup evidence. A running empty setup has no active transport.</summary>
public sealed record DataChannelSetupSnapshot
{
    /// <summary>Gets the host-local identity used by lifecycle commands.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the full implementation type name, without constructing the setup.</summary>
    public required string ImplementationType { get; init; }
    /// <summary>Gets the workload owner or registration type; null denotes host infrastructure.</summary>
    public string? Owner { get; init; }
    /// <summary>Gets the frozen policy. Explicit start does not change Manual to Automatic.</summary>
    public required ActivationMode Mode { get; init; }
    /// <summary>Gets the latest lifecycle state.</summary>
    public required DataChannelSetupState State { get; init; }
    /// <summary>Gets successfully declared channel IDs, including stopped channels.</summary>
    public required IReadOnlyList<string> ChannelIds { get; init; }
    /// <summary>Gets the latest lifecycle failure, or null after a successful operation.</summary>
    public string? Error { get; init; }
}
