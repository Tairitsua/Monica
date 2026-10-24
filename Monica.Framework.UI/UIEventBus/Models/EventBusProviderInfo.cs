using System.Globalization;
using Monica.EventBus.Abstractions;
using Monica.Core.Modularity.Diagnostics.Models;

namespace Monica.Framework.UI.UIEventBus.Models;

/// <summary>
/// Represents registered EventBus Provider information
/// </summary>
public class EventBusProviderInfo
{
    /// <summary>
    /// Service key (null indicates a non-Keyed default Provider)
    /// </summary>
    public string? ServiceKey { get; init; }

    /// <summary>
    /// Provider type
    /// </summary>
    public EventBusProviderKind ProviderType { get; init; }

    /// <summary>
    /// Provider capabilities
    /// </summary>
    public EventBusProviderCapabilities Capabilities { get; init; }

    /// <summary>
    /// Whether it is distributed EventBus
    /// </summary>
    public bool IsDistributed { get; init; }

    /// <summary>
    /// Declared provider module type name, or unknown when registration metadata is ambiguous.
    /// Reading this value never constructs the provider or establishes connection health.
    /// </summary>
    public string ImplementationType { get; init; } = "";

    /// <summary>
    /// Gets the detached module-option diagnostics target associated with this provider, when a provider module owns
    /// its configuration.
    /// </summary>
    public ModuleOptionDiagnosticsTarget? OptionDiagnosticsTarget { get; init; }

    /// <summary>Gets whether this provider has a module-option diagnostics source.</summary>
    public bool HasOptionDiagnostics => OptionDiagnosticsTarget is not null;

    /// <summary>
    /// Total number of subscriptions
    /// </summary>
    public int SubscriptionCount { get; set; }

    /// <summary>
    /// Number of active subscriptions
    /// </summary>
    public int ActiveSubscriptionCount { get; set; }

    /// <summary>
    /// Number of this provider's topic subscriptions currently recovering or terminally failed.
    /// Always zero for local providers.
    /// </summary>
    public int UnhealthyTopicCount { get; set; }

    /// <summary>
    /// Check if the Provider supports batch publishing
    /// </summary>
    public bool SupportsBulkPublish => Capabilities.HasFlag(EventBusProviderCapabilities.BulkPublish);

    /// <summary>
    /// Check if the Provider supports streaming subscriptions
    /// </summary>
    public bool SupportsStreaming => Capabilities.HasFlag(EventBusProviderCapabilities.Streaming);

    /// <summary>
    /// Check if the Provider supports dead letter queues
    /// </summary>
    public bool SupportsDeadLetterQueue => Capabilities.HasFlag(EventBusProviderCapabilities.DeadLetterQueue);

    /// <summary>
    /// Gets an opaque, ordinal identity for comparison and selection, including provider kind, bus scope, and key.
    /// </summary>
    /// <remarks>
    /// The default provider's null key differs from every named key, including "default" and an empty key.
    /// Named keys retain exact UTF-16 code units and case. Consumers must not parse or display the encoding;
    /// use <see cref="ServiceKey"/> and <see cref="IsDistributed"/> for routing and labels.
    /// </remarks>
    public string UniqueId => $"{(int)ProviderType}:{(IsDistributed ? "dist" : "local")}:" +
        (ServiceKey is null
            ? "n"
            : "k" + string.Concat(ServiceKey.Select(static value => ((int)value).ToString("x4", CultureInfo.InvariantCulture))));
}
