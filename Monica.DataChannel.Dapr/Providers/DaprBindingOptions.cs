using Monica.DataChannel.Abstractions.Communication;
using Monica.Tool.Extensions;

namespace Monica.DataChannel.Dapr.Providers;

/// <summary>
/// Configures an endpoint supplied by the opt-in Monica.DataChannel.Dapr integration.
/// Select <c>AddDataChannel().UseDaprBindings()</c> before materializing a binding pipeline.
/// </summary>
public class DaprBindingOptions : CommunicationOptions<DaprBindingEndpoint>
{
    /// <summary>Gets the Dapr binding component kind selected at construction.</summary>
    public EDaprBindingType DaprBindingType { get; }

    /// <summary>
    /// Required for MQ bindings. Input bindings must match metadata.route and allow Dapr to determine push targets by inspecting interfaces.
    /// </summary>
    public string? InputListenerRoute { get; set; }

    /// <summary>
    /// Required for MQ bindings using an output binding; should match metadata.name.
    /// </summary>
    public string? OutputBindingName { get; set; }

    /// <summary>
    /// Gets or sets whether input binding messages should be offered to <see cref="Monica.DataChannel.Dapr.Abstractions.IDaprBindingInputDispatcher"/>.
    /// The default is <see langword="false"/>, which preserves the existing synchronous pipeline behavior.
    /// Enable this only when the dispatcher preserves Dapr ACK semantics by either completing processing before returning
    /// or durably accepting the message before returning success.
    /// </summary>
    public bool EnableInputDispatcher { get; set; }

    /// <summary>
    /// Gets or sets whether output binding calls should include partition metadata when a
    /// <see cref="Monica.DataChannel.Abstractions.Partitioning.IDataChannelPartitionKeyResolver"/> returns a key.
    /// The default is <see langword="true"/>; without a registered resolver no metadata is emitted.
    /// </summary>
    public bool EnableOutputPartitionMetadata { get; set; } = true;

    /// <inheritdoc />
    public override void EnrichOrValidate()
    {
        switch (DaprBindingType)
        {
            case EDaprBindingType.MQTT3:
            case EDaprBindingType.Kafka:
                Type = CommunicationType.MQ;
                if (!Direction.EqualsAny(ConnectionDirection.Output, ConnectionDirection.Input))
                    throw new InvalidOperationException(
                        $"{DaprBindingType} supports only the {ConnectionDirection.Output} or {ConnectionDirection.Input} direction.");
                if (Direction == ConnectionDirection.Input && string.IsNullOrWhiteSpace(InputListenerRoute))
                {
                    throw new InvalidOperationException(
                        $"{nameof(InputListenerRoute)} must be provided when the direction is {ConnectionDirection.Input}.");
                }

                if (Direction == ConnectionDirection.Output && string.IsNullOrWhiteSpace(OutputBindingName))
                {
                    throw new InvalidOperationException(
                        $"{nameof(OutputBindingName)} must be provided when the direction is {ConnectionDirection.Output}.");
                }
                break;
            case EDaprBindingType.Cron:
                Type = CommunicationType.Trigger;
                if (!Direction.EqualsAny(ConnectionDirection.Input))
                    throw new InvalidOperationException(
                        $"{DaprBindingType} supports only the {ConnectionDirection.Input} direction.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(DaprBindingType), DaprBindingType, null);
        }
    }


    /// <summary>Creates binding options with an explicit component kind and supported direction.</summary>
    /// <param name="type">The Dapr component kind.</param>
    /// <param name="direction">The pipeline direction; MQTT and Kafka accept input or output, and Cron accepts input.</param>
    public DaprBindingOptions(EDaprBindingType type, ConnectionDirection direction)
    {
        Direction = direction;
        DaprBindingType = type;
    }
}

/// <summary>Identifies a Dapr component kind supported by binding options.</summary>
public enum EDaprBindingType
{
    /// <summary>A Dapr MQTT 3 input or output binding.</summary>
    MQTT3,
    /// <summary>A Dapr Kafka input or output binding.</summary>
    Kafka,
    /// <summary>A Dapr Cron trigger.</summary>
    Cron,
}
