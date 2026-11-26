using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Monica.DependencyInjection.Models;
using Monica.DependencyInjection.Models.Internal;
using Monica.Tool.Extensions;

namespace Monica.DependencyInjection.Services.Support;

/// <summary>
/// Tracks conventional-registration metadata and materializes a final diagnostics snapshot from the service collection.
/// </summary>
internal sealed class DependencyInjectionDiagnosticsRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<ServiceDescriptor, ConventionalRegistrationRecord> _recordsByDescriptor =
        new(new ServiceDescriptorReferenceComparer());
    private readonly List<ConventionalRegistrationRecord> _records = [];
    private readonly List<DependencyInjectionAutoRegistrationIssueInfo> _standaloneIssues = [];

    private IServiceCollection? _services;
    private DependencyInjectionDiagnosticsSnapshot? _snapshot;

    /// <summary>
    /// Binds the registry to the shared service collection instance used during application registration.
    /// </summary>
    public void BindServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        lock (_sync)
        {
            _services = services;
            _snapshot = null;
        }
    }

    /// <summary>
    /// Records one successful Monica conventional-registration descriptor.
    /// </summary>
    public void RecordConventionalRegistration(
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
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(sourceImplementationType);
        ArgumentNullException.ThrowIfNull(exposedServices);
        ArgumentNullException.ThrowIfNull(issues);

        lock (_sync)
        {
            var record = new ConventionalRegistrationRecord(
                descriptor,
                activation,
                sourceImplementationType,
                lifetime,
                lifetimeSource,
                registrationMode,
                outcome,
                exposedServices,
                issues);

            _records.Add(record);
            _recordsByDescriptor[descriptor] = record;
            _snapshot = null;
        }
    }

    /// <summary>
    /// Records automatic-registration issues that did not produce any final service descriptor.
    /// </summary>
    public void RecordStandaloneAutoRegistrationIssues(IReadOnlyList<DependencyInjectionAutoRegistrationIssueInfo> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        if (issues.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            _standaloneIssues.AddRange(issues);
            _snapshot = null;
        }
    }

    /// <summary>
    /// Gets the finalized diagnostics snapshot, building it once from the final service collection.
    /// </summary>
    public DependencyInjectionDiagnosticsSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            if (_snapshot != null)
            {
                return _snapshot;
            }

            if (_services == null)
            {
                throw new InvalidOperationException("Dependency-injection diagnostics are not bound to a service collection.");
            }

            _snapshot = BuildSnapshot(_services, _recordsByDescriptor, _records, _standaloneIssues);
            return _snapshot;
        }
    }

    private static DependencyInjectionDiagnosticsSnapshot BuildSnapshot(
        IServiceCollection services,
        IReadOnlyDictionary<ServiceDescriptor, ConventionalRegistrationRecord> directRecords,
        IReadOnlyList<ConventionalRegistrationRecord> allRecords,
        IReadOnlyList<DependencyInjectionAutoRegistrationIssueInfo> standaloneIssues)
    {
        var descriptors = new List<DependencyInjectionDescriptorInfo>(services.Count);
        var identityIndices = new Dictionary<ServiceIdentifier, int>();
        // Reserve direct matches before considering logical matches in collection order.
        var matchedRecords = new HashSet<ConventionalRegistrationRecord>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];
            identityIndices.TryAdd(new ServiceIdentifier(descriptor.ServiceKey, descriptor.ServiceType), index);
            if (directRecords.TryGetValue(descriptor, out var directRecord))
            {
                matchedRecords.Add(directRecord);
            }
        }
        var autoRegistrationIssues = allRecords
            .SelectMany(record => record.Issues)
            .Concat(standaloneIssues)
            .Distinct(new AutoRegistrationIssueReferenceComparer())
            .OrderByDescending(item => item.Severity == DependencyInjectionDiagnosticSeverity.Error)
            .ThenBy(item => item.SourceImplementationTypeDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];
            var isDirectMatch = directRecords.TryGetValue(descriptor, out var record);

            if (record == null)
            {
                record = FindLogicalMatch(descriptor, allRecords, matchedRecords);
            }

            if (record != null)
            {
                matchedRecords.Add(record);
            }

            descriptors.Add(CreateDescriptorInfo(index, descriptor, record, isDirectMatch, identityIndices));
        }

        return new DependencyInjectionDiagnosticsSnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Descriptors = descriptors,
            TotalDescriptorCount = descriptors.Count,
            AutoRegistrationIssues = autoRegistrationIssues,
            AutoRegisteredDescriptorCount = descriptors.Count(item => item.IsAutoRegistered),
            KeyedDescriptorCount = descriptors.Count(item => item.IsKeyedService),
            FactoryDescriptorCount = descriptors.Count(item => item.ImplementationKind == DependencyInjectionDescriptorImplementationKind.Factory),
            InstanceDescriptorCount = descriptors.Count(item => item.ImplementationKind == DependencyInjectionDescriptorImplementationKind.Instance),
            AutoRegistrationWarningCount = autoRegistrationIssues.Count(item => item.Severity == DependencyInjectionDiagnosticSeverity.Warning),
            AutoRegistrationErrorCount = autoRegistrationIssues.Count(item => item.Severity == DependencyInjectionDiagnosticSeverity.Error)
        };
    }

    private static ConventionalRegistrationRecord? FindLogicalMatch(
        ServiceDescriptor descriptor,
        IReadOnlyList<ConventionalRegistrationRecord> records,
        IReadOnlySet<ConventionalRegistrationRecord> matchedRecords)
    {
        var implementationType = descriptor.GetResolvedImplementationType();

        return records.FirstOrDefault(record =>
            !matchedRecords.Contains(record) &&
            record.Lifetime == descriptor.Lifetime &&
            descriptor.MatchesServiceIdentity(record.Descriptor.ServiceType, record.Descriptor.ServiceKey) &&
            implementationType == record.SourceImplementationType);
    }

    private static DependencyInjectionDescriptorInfo CreateDescriptorInfo(
        int index,
        ServiceDescriptor descriptor,
        ConventionalRegistrationRecord? record,
        bool isDirectMatch,
        IReadOnlyDictionary<ServiceIdentifier, int> identityIndices)
    {
        var implementationType = isDirectMatch ? record!.SourceImplementationType : descriptor.GetResolvedImplementationType();
        var implementationKind = descriptor.GetImplementationKind();
        var hasWarningIssues = record?.Issues.Any(item => item.Severity == DependencyInjectionDiagnosticSeverity.Warning) ?? false;
        var hasErrorIssues = record?.Issues.Any(item => item.Severity == DependencyInjectionDiagnosticSeverity.Error) ?? false;

        return new DependencyInjectionDescriptorInfo
        {
            Index = index,
            ServiceIdentityIndex = identityIndices[new ServiceIdentifier(descriptor.ServiceKey, descriptor.ServiceType)],
            ServiceType = descriptor.ServiceType.GetCleanFullName(),
            ServiceTypeDisplayName = descriptor.ServiceType.GetCleanName(),
            ServiceAssemblyName = descriptor.ServiceType.Assembly.GetName().Name,
            Lifetime = descriptor.Lifetime,
            IsKeyedService = descriptor.IsKeyedService,
            ServiceKey = descriptor.GetServiceKeyDisplay(),
            ImplementationKind = implementationKind,
            Activation = isDirectMatch
                ? record!.Activation
                : implementationKind == DependencyInjectionDescriptorImplementationKind.Factory
                    ? DependencyInjectionActivation.Factory
                    : DependencyInjectionActivation.Direct,
            ImplementationType = implementationType?.GetCleanFullName(),
            ImplementationTypeDisplayName = implementationType?.GetCleanName(),
            ImplementationAssemblyName = implementationType?.Assembly.GetName().Name,
            FactoryDisplay = descriptor.GetFactoryDisplay(),
            IsOpenGeneric = descriptor.ServiceType.IsGenericTypeDefinition || (implementationType?.IsGenericTypeDefinition ?? false),
            IsAutoRegistered = record != null,
            IsDirectConventionMatch = isDirectMatch,
            AutoRegistration = record == null ? null : CreateAutoRegistrationInfo(record, identityIndices),
            HasWarningIssues = hasWarningIssues,
            HasErrorIssues = hasErrorIssues
        };
    }

    private static DependencyInjectionAutoRegistrationInfo CreateAutoRegistrationInfo(
        ConventionalRegistrationRecord record,
        IReadOnlyDictionary<ServiceIdentifier, int> identityIndices)
    {
        return new DependencyInjectionAutoRegistrationInfo
        {
            SourceImplementationType = record.SourceImplementationType.GetCleanFullName(),
            SourceImplementationTypeDisplayName = record.SourceImplementationType.GetCleanName(),
            SourceImplementationAssemblyName = record.SourceImplementationType.Assembly.GetName().Name,
            Lifetime = record.Lifetime,
            LifetimeSource = record.LifetimeSource,
            RegistrationMode = record.RegistrationMode,
            Outcome = record.Outcome,
            ExposedServices = record.ExposedServices
                .Select(item => new DependencyInjectionExposedServiceInfo
                {
                    ServiceIdentityIndex = identityIndices.TryGetValue(item, out var index) ? index : null,
                    ServiceType = item.ServiceType.GetCleanFullName(),
                    ServiceTypeDisplayName = item.ServiceType.GetCleanName(),
                    IsKeyedService = item.ServiceKey != null,
                    ServiceKey = ServiceDescriptorDiagnosticsExtensions.FormatServiceKey(item.ServiceKey)
                })
                .ToArray(),
            Issues = record.Issues
        };
    }

    private sealed class ServiceDescriptorReferenceComparer : IEqualityComparer<ServiceDescriptor>
    {
        public bool Equals(ServiceDescriptor? x, ServiceDescriptor? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(ServiceDescriptor obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }

    private sealed class AutoRegistrationIssueReferenceComparer : IEqualityComparer<DependencyInjectionAutoRegistrationIssueInfo>
    {
        public bool Equals(DependencyInjectionAutoRegistrationIssueInfo? x, DependencyInjectionAutoRegistrationIssueInfo? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(DependencyInjectionAutoRegistrationIssueInfo obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
