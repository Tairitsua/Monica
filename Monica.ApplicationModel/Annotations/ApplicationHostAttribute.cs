namespace Monica.ApplicationModel.Annotations;

/// <summary>Generates application ownership, local implementation bindings, API callers, and owned HTTP receivers in this host.</summary>
/// <remarks>Reference the desired domain/role libraries and the Monica API source generator. Partial hosts also import the generated product catalog.</remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ApplicationHostAttribute : Attribute;

/// <summary>Identifies the generated manifest provider for an application host.</summary>
/// <param name="providerType">A public parameterless implementation of IApplicationManifest.</param>
/// <remarks>This declaration is emitted by the source generator rather than maintained by application developers.</remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GeneratedApplicationManifestAttribute(Type providerType) : Attribute
{
    /// <summary>Gets the manifest provider type.</summary>
    public Type ProviderType { get; } = providerType;
}

/// <summary>Associates a generated receiver action with its application request for publication selection.</summary>
/// <param name="requestType">The request implemented by the action.</param>
/// <remarks>Generator-owned metadata; developers continue to declare HTTP mapping only through ApiEndpoint.</remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ApplicationApiReceiverAttribute(Type requestType) : Attribute
{
    /// <summary>Gets the request implemented by the receiver.</summary>
    public Type RequestType { get; } = requestType;
}
