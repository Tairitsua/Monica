using Microsoft.Extensions.DependencyInjection;
using Monica.DependencyInjection.Models;
using Monica.DependencyInjection.Models.Internal;

namespace Monica.DependencyInjection.Services.Support;

/// <summary>
/// Stores the metadata Monica captured when conventional registration successfully applied a descriptor.
/// </summary>
internal sealed class ConventionalRegistrationRecord(
    ServiceDescriptor descriptor,
    DependencyInjectionActivation activation,
    Type sourceImplementationType,
    ServiceLifetime lifetime,
    DependencyInjectionLifetimeSource lifetimeSource,
    DependencyInjectionAutoRegistrationMode registrationMode,
    DependencyInjectionAutoRegistrationOutcome outcome,
    IReadOnlyList<ServiceIdentifier> exposedServices,
    IReadOnlyList<DependencyInjectionAutoRegistrationIssueInfo> issues)
{
    /// <summary>
    /// Gets the descriptor associated with the record.
    /// </summary>
    public ServiceDescriptor Descriptor { get; } = descriptor;

    /// <summary>Gets how the registrar constructed this descriptor.</summary>
    public DependencyInjectionActivation Activation { get; } = activation;

    /// <summary>
    /// Gets the implementation type Monica discovered during conventional registration.
    /// </summary>
    public Type SourceImplementationType { get; } = sourceImplementationType;

    /// <summary>
    /// Gets the lifetime Monica applied.
    /// </summary>
    public ServiceLifetime Lifetime { get; } = lifetime;

    /// <summary>
    /// Gets the source Monica used to resolve the lifetime.
    /// </summary>
    public DependencyInjectionLifetimeSource LifetimeSource { get; } = lifetimeSource;

    /// <summary>
    /// Gets the service-collection mutation mode Monica used.
    /// </summary>
    public DependencyInjectionAutoRegistrationMode RegistrationMode { get; } = registrationMode;

    /// <summary>
    /// Gets the applied result of the successful registration.
    /// </summary>
    public DependencyInjectionAutoRegistrationOutcome Outcome { get; } = outcome;

    /// <summary>
    /// Gets the full service-identity group Monica exposed for the implementation.
    /// </summary>
    public IReadOnlyList<ServiceIdentifier> ExposedServices { get; } = exposedServices;

    /// <summary>
    /// Gets the warnings Monica emitted while evaluating this registration.
    /// </summary>
    public IReadOnlyList<DependencyInjectionAutoRegistrationIssueInfo> Issues { get; } = issues;
}
