using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.Modularity.Services.Support;
using Monica.Core.TypeDiscovery.Models;
using Monica.DependencyInjection.Abstractions;
using Monica.DependencyInjection.Annotations;
using Monica.DependencyInjection.Models;
using Monica.DependencyInjection.Models.Internal;
using Monica.DependencyInjection.Services.Support;
using Monica.Modules;
using Monica.Tool.Extensions;

namespace Monica.DependencyInjection.Services;
/// <summary>
/// Registers discovered Monica services using lifetime markers and exposure attributes.
/// </summary>
internal sealed class ConventionalRegistrar(
    ModuleDependencyInjectionOption option,
    ILogger logger,
    DependencyInjectionDiagnosticsRegistry? diagnosticsRegistry = null)
{
    /// <summary>
    /// Registers a single type into the service collection based on its attributes and lifetime.
    /// </summary>
    /// <param name="registrations">The indexed writer that owns discovery-phase mutations.</param>
    /// <param name="match">The discovered type and its host-scoped cached structural facts.</param>
    internal void AddType(
        ModuleServiceRegistrationWriter registrations,
        BusinessTypeMatch match)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(match);

        var shape = match.Shape;
        var type = match.Type;

        // TODO: Support automatic registration of generic types through configuration.
        // Conventional registration deliberately excludes every generic type, including closed constructions.
        if (type.IsGenericType)
        {
            return;
        }

        var inheritedAttributes = shape.GetAttributes(typeof(Attribute), inherit: true);
        var dependencyAttribute = inheritedAttributes.OfType<DependencyAttribute>().FirstOrDefault();
        var (lifetime, lifetimeSource) = ResolveLifetime(shape, dependencyAttribute);
        if (lifetime == null)
        {
            return;
        }

        var typeName = type.Name;
        var registrationMode = ResolveRegistrationMode(dependencyAttribute);
        var shouldLog = option.EnableAutoRegistrationLogging;
        var shouldEmitDiagnostics = option.EnableAutoRegistrationDiagnostics;
        var shouldCaptureDiagnostics = shouldEmitDiagnostics && diagnosticsRegistry != null;

        var exposedServiceAndKeyedServiceTypes = ExposedServiceExplorer
            .GetExposedKeyedServices(inheritedAttributes)
            .Concat(ExposedServiceExplorer
                .GetExposedServices(shape, inheritedAttributes)
                .Select(serviceType => new ServiceIdentifier(serviceType)))
            .ToList();
        var exposedServicesByKey = exposedServiceAndKeyedServiceTypes.ToLookup(item => item.ServiceKey);
        var autoRegistrationIssues = shouldLog || shouldEmitDiagnostics
            ? CreateAutoRegistrationIssues(type, lifetime.Value, lifetimeSource, exposedServiceAndKeyedServiceTypes)
            : [];

        if (shouldLog)
        {
            if (exposedServiceAndKeyedServiceTypes.Count == 0)
            {
                logger.LogError("Failed to auto-register type: {TypeName} {Lifetime}", typeName, lifetime);
            }
            else if (autoRegistrationIssues.Any(item => item.Kind == DependencyInjectionAutoRegistrationIssueKind.ConcreteTypeOnlyExposure))
            {
                logger.LogWarning("Only the concrete type was registered: {TypeName} {Lifetime}", typeName, lifetime);
            }
            else
            {
                logger.LogInformation("Auto-registered: {TypeName}->{ServiceTypes} {Lifetime}",
                    typeName,
                    $"[{exposedServiceAndKeyedServiceTypes.Select(p => p.ServiceType.Name).StringJoin(", ")}]",
                    lifetime);
            }
        }

        if (shouldCaptureDiagnostics && exposedServiceAndKeyedServiceTypes.Count == 0 && autoRegistrationIssues.Count > 0)
        {
            diagnosticsRegistry!.RecordStandaloneAutoRegistrationIssues(autoRegistrationIssues);
        }
        
        foreach (var exposedServiceType in exposedServiceAndKeyedServiceTypes)
        {
            var allExposingServiceTypes = exposedServicesByKey[exposedServiceType.ServiceKey].ToList();
            var hadExistingDescriptor = shouldCaptureDiagnostics && registrations.Contains(
                exposedServiceType.ServiceType,
                exposedServiceType.ServiceKey,
                isKeyedService: exposedServiceType.ServiceKey is not null);
            var (serviceDescriptor, activation) = CreateServiceDescriptor(
                shape,
                exposedServiceType.ServiceKey,
                exposedServiceType.ServiceType,
                allExposingServiceTypes,
                lifetime.Value
            );
            var descriptorWasAdded = ApplyRegistrationMode(
                registrations,
                serviceDescriptor,
                registrationMode);

            if (shouldCaptureDiagnostics && descriptorWasAdded)
            {
                diagnosticsRegistry!.RecordConventionalRegistration(
                    serviceDescriptor,
                    activation,
                    type,
                    lifetime.Value,
                    lifetimeSource,
                    registrationMode,
                    hadExistingDescriptor && registrationMode == DependencyInjectionAutoRegistrationMode.Replace
                        ? DependencyInjectionAutoRegistrationOutcome.ReplacedExisting
                        : DependencyInjectionAutoRegistrationOutcome.Added,
                    [.. exposedServiceAndKeyedServiceTypes],
                    autoRegistrationIssues);
            }
        }
    }
    private static (
        ServiceLifetime? Lifetime,
        DependencyInjectionLifetimeSource Source) ResolveLifetime(
        BusinessTypeShape shape,
        DependencyAttribute? dependencyAttribute)
    {
        if (dependencyAttribute?.Lifetime is { } attributeLifetime)
        {
            return (attributeLifetime, DependencyInjectionLifetimeSource.DependencyAttribute);
        }

        if (shape.IsAssignableTo(typeof(ITransientDependency)))
        {
            return (
                ServiceLifetime.Transient,
                DependencyInjectionLifetimeSource.TransientMarkerInterface);
        }

        if (shape.IsAssignableTo(typeof(ISingletonDependency)))
        {
            return (
                ServiceLifetime.Singleton,
                DependencyInjectionLifetimeSource.SingletonMarkerInterface);
        }

        if (shape.IsAssignableTo(typeof(IScopedDependency)))
        {
            return (
                ServiceLifetime.Scoped,
                DependencyInjectionLifetimeSource.ScopedMarkerInterface);
        }

        return (null, DependencyInjectionLifetimeSource.Unknown);
    }

    private static DependencyInjectionAutoRegistrationMode ResolveRegistrationMode(
        DependencyAttribute? dependencyAttribute)
    {
        if (dependencyAttribute?.ReplaceServices == true)
        {
            return DependencyInjectionAutoRegistrationMode.Replace;
        }

        if (dependencyAttribute?.TryRegister == true)
        {
            return DependencyInjectionAutoRegistrationMode.TryAdd;
        }

        return DependencyInjectionAutoRegistrationMode.Add;
    }

    private static bool ApplyRegistrationMode(
        ModuleServiceRegistrationWriter registrations,
        ServiceDescriptor descriptor,
        DependencyInjectionAutoRegistrationMode registrationMode)
    {
        switch (registrationMode)
        {
            case DependencyInjectionAutoRegistrationMode.Replace:
                registrations.Replace(descriptor);
                return true;
            case DependencyInjectionAutoRegistrationMode.TryAdd:
                return registrations.TryAdd(descriptor);
            default:
                registrations.Add(descriptor);
                return true;
        }
    }

    private static IReadOnlyList<DependencyInjectionAutoRegistrationIssueInfo> CreateAutoRegistrationIssues(
        Type sourceImplementationType,
        ServiceLifetime lifetime,
        DependencyInjectionLifetimeSource lifetimeSource,
        IReadOnlyList<ServiceIdentifier> exposedServices)
    {
        if (exposedServices.Count == 0)
        {
            return [CreateAutoRegistrationIssue(
                sourceImplementationType,
                lifetime,
                lifetimeSource,
                DependencyInjectionAutoRegistrationIssueKind.MissingExposedServices,
                exposedServices)];
        }

        if (exposedServices is [{ ServiceType: { } serviceType }] && serviceType == sourceImplementationType)
        {
            return [CreateAutoRegistrationIssue(
                sourceImplementationType,
                lifetime,
                lifetimeSource,
                DependencyInjectionAutoRegistrationIssueKind.ConcreteTypeOnlyExposure,
                exposedServices)];
        }

        return [];
    }

    private static DependencyInjectionAutoRegistrationIssueInfo CreateAutoRegistrationIssue(
        Type sourceImplementationType,
        ServiceLifetime lifetime,
        DependencyInjectionLifetimeSource lifetimeSource,
        DependencyInjectionAutoRegistrationIssueKind kind,
        IReadOnlyList<ServiceIdentifier> exposedServices)
    {
        return new DependencyInjectionAutoRegistrationIssueInfo
        {
            Severity = kind == DependencyInjectionAutoRegistrationIssueKind.MissingExposedServices
                ? DependencyInjectionDiagnosticSeverity.Error
                : DependencyInjectionDiagnosticSeverity.Warning,
            Kind = kind,
            SourceImplementationType = sourceImplementationType.GetCleanFullName(),
            SourceImplementationTypeDisplayName = sourceImplementationType.GetCleanName(),
            SourceImplementationAssemblyName = sourceImplementationType.Assembly.GetName().Name,
            Lifetime = lifetime,
            LifetimeSource = lifetimeSource,
            ExposedServices = exposedServices
                .Select(item => new DependencyInjectionExposedServiceInfo
                {
                    ServiceType = item.ServiceType.GetCleanFullName(),
                    ServiceTypeDisplayName = item.ServiceType.GetCleanName(),
                    IsKeyedService = item.ServiceKey != null,
                    ServiceKey = ServiceDescriptorDiagnosticsExtensions.FormatServiceKey(item.ServiceKey)
                })
                .ToArray()
        };
    }

    /// <summary>
    /// Creates a <see cref="ServiceDescriptor"/> for the specified implementation and service type.
    /// </summary>
    /// <param name="implementationShape">The cached structural facts for the implementation type.</param>
    /// <param name="serviceKey">The key associated with the service, if any.</param>
    /// <param name="exposingServiceType">The type of the service being exposed.</param>
    /// <param name="allExposingServiceTypes">All service types being exposed.</param>
    /// <param name="lifetime">The lifetime of the service.</param>
    /// <returns>A <see cref="ServiceDescriptor"/> for the service.</returns>
    private static (ServiceDescriptor Descriptor, DependencyInjectionActivation Activation) CreateServiceDescriptor(
        BusinessTypeShape implementationShape,
        object? serviceKey,
        Type exposingServiceType,
        List<ServiceIdentifier> allExposingServiceTypes,
        ServiceLifetime lifetime)
    {
        var implementationType = implementationShape.Type;
        var requiresCachedServiceProviderAccess = RequiresCachedServiceProviderAccess(implementationShape);

        if (requiresCachedServiceProviderAccess && lifetime == ServiceLifetime.Singleton)
        {
            throw new InvalidOperationException(
                $"{implementationType.FullName} can not be registered as Singleton because it requires {nameof(ICachedServiceProvider)}.");
        }

        // TODO: Revisit whether this redirection block is still necessary.
        if (lifetime.EqualsAny(ServiceLifetime.Singleton, ServiceLifetime.Scoped))
        {
            var redirectedType = GetRedirectedTypeOrNull(
                implementationType,
                exposingServiceType,
                allExposingServiceTypes
            );
            if (redirectedType != null)
            {
                var descriptor = serviceKey == null
                    ? ServiceDescriptor.Describe(
                        exposingServiceType,
                        provider => provider.GetService(redirectedType)!,
                        lifetime
                    )
                    : ServiceDescriptor.DescribeKeyed(
                        exposingServiceType,
                        serviceKey,
                        (provider, key) =>
                        {
                            if (provider is IKeyedServiceProvider keyedServiceProvider)
                            {
                                return keyedServiceProvider.GetKeyedService(redirectedType, key)!;
                            }

                            throw new InvalidOperationException("This service provider doesn't support keyed services.");
                        },
                        lifetime
                    );
                return (descriptor, DependencyInjectionActivation.Forward);
            }
        }
        var direct = CreateDirectServiceDescriptor(
            implementationType,
            serviceKey,
            exposingServiceType,
            lifetime,
            requiresCachedServiceProviderAccess);
        return (direct, requiresCachedServiceProviderAccess
            ? DependencyInjectionActivation.MonicaActivated
            : DependencyInjectionActivation.Direct);
    }

    private static bool RequiresCachedServiceProviderAccess(BusinessTypeShape implementationShape)
    {
        return implementationShape.IsAssignableTo(typeof(ICachedServiceProviderAccessor));
    }

    private static ServiceDescriptor CreateDirectServiceDescriptor(
        Type implementationType,
        object? serviceKey,
        Type exposingServiceType,
        ServiceLifetime lifetime,
        bool requiresCachedServiceProviderAccess)
    {
        if (!requiresCachedServiceProviderAccess)
        {
            return serviceKey == null
                ? ServiceDescriptor.Describe(
                    exposingServiceType,
                    implementationType,
                    lifetime
                )
                : ServiceDescriptor.DescribeKeyed(
                    exposingServiceType,
                    serviceKey,
                    implementationType,
                    lifetime
                );
        }

        return serviceKey == null
            ? ServiceDescriptor.Describe(
                exposingServiceType,
                provider => CreateImplementationInstance(provider, implementationType),
                lifetime
            )
            : ServiceDescriptor.DescribeKeyed(
                exposingServiceType,
                serviceKey,
                (provider, _) => CreateImplementationInstance(provider, implementationType),
                lifetime
            );
    }

    private static object CreateImplementationInstance(IServiceProvider provider, Type implementationType)
    {
        var instance = ActivatorUtilities.CreateInstance(provider, implementationType);

        if (instance is ICachedServiceProviderAccessor accessor)
        {
            accessor.CachedServiceProvider = provider.GetRequiredService<ICachedServiceProvider>();
        }

        return instance;
    }
    /// <summary>
    /// Determines the redirected type for a service, if applicable.
    /// </summary>
    /// <param name="implementationType">The type implementing the service.</param>
    /// <param name="exposingServiceType">The type of the service being exposed.</param>
    /// <param name="allExposingKeyedServiceTypes">All keyed service types being exposed.</param>
    /// <returns>The redirected type, if applicable; otherwise, null.</returns>
    private static Type? GetRedirectedTypeOrNull(
        Type implementationType,
        Type exposingServiceType,
        List<ServiceIdentifier> allExposingKeyedServiceTypes)
    {
        if (allExposingKeyedServiceTypes.Count < 2)
        {
            return null;
        }
        if (exposingServiceType == implementationType)
        {
            return null;
        }
        if (allExposingKeyedServiceTypes.Any(t => t.ServiceType == implementationType))
        {
            return implementationType;
        }
        return allExposingKeyedServiceTypes.FirstOrDefault(
            t => t.ServiceType != exposingServiceType && exposingServiceType.IsAssignableFrom(t.ServiceType)
        ).ServiceType;
    }
}
