namespace Monica.Core.TypeDiscovery.Abstractions;

/// <summary>Describes whether an included resource may run locally and initiate work without a request.</summary>
public enum ActivationMode
{
    /// <summary>The resource has no eligible implementation or owner in this process.</summary>
    Disabled,
    /// <summary>The resource is locally eligible but only explicit, capability-specific work is permitted.</summary>
    Manual,
    /// <summary>The resource may perform its normal automatic work.</summary>
    Automatic
}

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
    Endpoint,
    /// <summary>A finite initializer with dependency and readiness semantics.</summary>
    Seeder,
    /// <summary>Background dispatch for one transactional outbox store.</summary>
    Outbox,
    /// <summary>A channel that independently accepts or sends external traffic.</summary>
    DataChannel
}

/// <summary>Supplies one host's immutable business activation policy to infrastructure consumers.</summary>
/// <remarks>
/// The application composition installs the same instance before discovery commits and registers it for runtime
/// guards. Core installs a default policy for standalone hosts using Application:AutonomousWork.
/// Module registrations and service-finalization callbacks cannot replace or remove that runtime singleton.
/// </remarks>
public interface ITypeActivationPolicy
{
    /// <summary>Gets this host's selected durable-work namespaces, including roles with no remaining job definitions.</summary>
    /// <remarks>Schedulers publish empty snapshots for selected owners whose last job was removed. Null means no application-owned inventory; an empty collection means the known inventory selects no owners.</remarks>
    IReadOnlyCollection<string>? ActiveWorkloadOwners => null;

    /// <summary>Gets local eligibility and automatic-execution permission for a resource.</summary>
    /// <param name="implementationType">The actual implementation; its initializer attributes are never read from the owner.</param>
    /// <param name="kind">The resource boundary being evaluated.</param>
    /// <param name="registrationOwner">The declaring domain/role or resource anchor for a shared implementation, when known.</param>
    /// <remarks>Known role ownership cannot be reassigned by a conflicting registration owner. Passive services remain available independently of background placement.</remarks>
    ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null);

    /// <summary>Rejects a local request whose execution requirements are not satisfied, before resolving its handler.</summary>
    void EnsureCanExecuteRequest(Type requestType);

    /// <summary>Gets stable durable-work ownership independent of deployment AppId, or null for ordinary infrastructure.</summary>
    /// <remarks>Applications normally use the role ID; migrations may preserve an existing scheduler namespace.</remarks>
    string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null);
}
