using System.Collections.ObjectModel;
using Monica.DataChannel.Abstractions.Communication;
using Monica.DataChannel.Abstractions.Pipeline;
using Monica.DataChannel.Middlewares;
using Monica.DataChannel.Models;
using Monica.DataChannel.Pipeline;
using Monica.DataChannel.Providers.Kafka;
using Monica.Kafka;
using Monica.Tool.Extensions;

namespace Monica.DataChannel.UIDataChannel.Models;

/// <summary>Detached component identity and evidence, captured eagerly without retaining the live component.</summary>
public class PipelineComponentInfo
{
    /// <summary>Captures standalone evidence without an owner-issued action key or stage ordinal.</summary>
    /// <param name="component">The component whose values are read immediately.</param>
    public PipelineComponentInfo(IPipelineComponent component)
        : this(component, component is IPipelineEndpoint endpoint
            ? endpoint.EntranceType == ChannelSide.Inner ? PipelineComponentStage.InnerEndpoint : PipelineComponentStage.OuterEndpoint
            : component is IPipelineEndpointMiddleware ? PipelineComponentStage.EndpointConfiguration : PipelineComponentStage.Transform,
            -1, string.Empty)
    {
    }

    internal PipelineComponentInfo(PipelineComponentRegistration registration)
        : this(registration.Component, registration.Stage, registration.Ordinal, registration.Key)
    {
    }

    private PipelineComponentInfo(IPipelineComponent component, PipelineComponentStage stage, int ordinal, string componentKey)
    {
        var componentType = component is TransientComponentProxyBase proxy ? proxy.DeclaredComponentType : component.GetType();
        Type = GetPipeComponentType(componentType);
        Stage = stage;
        Ordinal = ordinal;
        ComponentKey = componentKey;
        CapturedAt = DateTime.UtcNow;
        Name = componentType.GetCleanName();
        TypeName = componentType.GetCleanFullName();
        AssemblyQualifiedName = componentType.AssemblyQualifiedName ?? TypeName;
        IsTransient = component is TransientComponentProxyBase;
        CanDebugMessages = component is MessageDebuggerMiddleware && !IsTransient;
        object? metadata = null;
        Metadata = DataChannelValueSnapshot.Capture(() => metadata = (object?)component.GetMetadata());
        if (metadata is KafkaEndpointMetadata { Options.Direction: ConnectionDirection.Input or ConnectionDirection.InputAndOutput } kafka)
        {
            KafkaInput = new(kafka.Options.ProcessingFailureAction, kafka.Options.ProcessingMaxAttempts,
                kafka.Options.DeadLetterTopic, kafka.Consumer);
        }
        if (component is PipelineInfoDisplayMiddlewareBase infoMiddleware)
        {
            InfoDictionary = new ReadOnlyDictionary<string, DataChannelValueSnapshot>(infoMiddleware.GetInfoDictionary()
                .ToDictionary(entry => entry.Key, entry => DataChannelValueSnapshot.Capture(entry.Value), StringComparer.Ordinal));
        }
    }

    /// <summary>Gets the classification of the declared CLR type, independent of stage membership.</summary>
    public PipelineComponentKind Type { get; }
    /// <summary>Gets the component's actual registration stage.</summary>
    public PipelineComponentStage Stage { get; }
    /// <summary>Gets the zero-based index within the stage, or -1 for a standalone capture.</summary>
    public int Ordinal { get; }
    /// <summary>Gets the opaque channel-local registration key, or empty for a standalone capture.</summary>
    public string ComponentKey { get; }
    /// <summary>Gets the UTC time when component evidence capture began.</summary>
    public DateTime CapturedAt { get; }
    /// <summary>Gets the declared concrete CLR type's normalized display name.</summary>
    public string Name { get; }
    /// <summary>Gets the declared concrete CLR type's normalized full display name.</summary>
    public string TypeName { get; }
    /// <summary>Gets the declared concrete CLR type's assembly-qualified identity.</summary>
    public string AssemblyQualifiedName { get; }
    /// <summary>Gets whether the component resolves fresh scoped instances for each operation.</summary>
    public bool IsTransient { get; }
    /// <summary>Gets whether this registration owns a persistent debugger queue, including derived debuggers.</summary>
    public bool CanDebugMessages { get; }
    /// <summary>Gets complete detached metadata or its local capture failure.</summary>
    public DataChannelValueSnapshot Metadata { get; }
    internal KafkaInputDetails? KafkaInput { get; }
    /// <summary>Gets detached information values for persistent information-display middleware, if present.</summary>
    public IReadOnlyDictionary<string, DataChannelValueSnapshot>? InfoDictionary { get; }

    /// <summary>Classifies the supplied declared concrete component type.</summary>
    /// <param name="type">The CLR type to classify.</param>
    /// <returns>The matching component kind, independent of processing stage.</returns>
    public static PipelineComponentKind GetPipeComponentType(Type type)
    {
        if (type.IsAssignableTo(typeof(IPipelineEndpoint))) return PipelineComponentKind.Endpoint;
        if (type.IsAssignableTo(typeof(PipelineInfoDisplayMiddlewareBase))) return PipelineComponentKind.InfoDisplayMiddleware;
        if (type.IsAssignableTo(typeof(IPipelineMonitorMiddleware))) return PipelineComponentKind.MonitorMiddleware;
        if (type.IsAssignableTo(typeof(IPipelineTransformMiddleware))) return PipelineComponentKind.TransformMiddleware;
        if (type.IsAssignableTo(typeof(IPipelineEndpointMiddleware))) return PipelineComponentKind.EndpointMiddleware;
        return PipelineComponentKind.BaseMiddleware;
    }
}

internal sealed record KafkaInputDetails(KafkaFailureAction Action, int MaxAttempts, string? DeadLetterTopic,
    KafkaConsumerSnapshot? Consumer);
