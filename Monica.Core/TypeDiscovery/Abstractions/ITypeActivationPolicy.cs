namespace Monica.Core.TypeDiscovery.Abstractions;

/// <summary>Identifies the registration boundary selecting a discovered business type.</summary>
public enum TypeActivationKind
{
    /// <summary>Conventional dependency-injection registration.</summary>
    Service,
    /// <summary>A local mediator request handler.</summary>
    RequestHandler,
    /// <summary>An external event subscription, independent of local event handling.</summary>
    ExternalSubscription,
    /// <summary>A recurring or triggered job executor.</summary>
    Job,
    /// <summary>A product-owned hosted service or startup entry.</summary>
    HostedService,
    /// <summary>A published receiver.</summary>
    Endpoint
}

/// <summary>Supplies one host's immutable business activation policy to infrastructure consumers.</summary>
/// <remarks>
/// The application composition installs the same instance before discovery commits and registers it for runtime
/// guards. Standalone infrastructure without a policy retains its normal registration behavior.
/// </remarks>
public interface ITypeActivationPolicy
{
    /// <summary>Gets this host's selected durable-work namespaces, including roles with no remaining job definitions.</summary>
    /// <remarks>Schedulers publish empty snapshots for selected owners whose last job was removed. Ordinary policies may leave this empty.</remarks>
    IReadOnlyCollection<string> ActiveWorkloadOwners => [];

    /// <summary>Whether the specified registration boundary may activate the implementation.</summary>
    bool IsEnabled(Type implementationType, TypeActivationKind kind);

    /// <summary>Rejects a local request whose execution requirements are not satisfied, before resolving its handler.</summary>
    void EnsureCanExecuteRequest(Type requestType);

    /// <summary>Gets stable durable-work ownership independent of deployment AppId, or null for ordinary infrastructure.</summary>
    /// <remarks>Applications normally use the role ID; migrations may preserve an existing scheduler namespace.</remarks>
    string? GetWorkloadOwner(Type implementationType);
}
