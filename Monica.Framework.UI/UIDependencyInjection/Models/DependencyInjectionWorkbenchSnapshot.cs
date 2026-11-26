using Microsoft.Extensions.DependencyInjection;
using Monica.DependencyInjection.Models;

namespace Monica.Framework.UI.UIDependencyInjection.Models;

/// <summary>Attention and advisory flags for the conventional-registration workbench.</summary>
[Flags]
public enum ConventionFinding
{
    Attention = 0,
    NotRegistered = 1,
    BypassesActivation = 2,
    ResolvesElsewhere = 4,
    RegisteredTwice = 8,
    ConcreteOnly = 16
}

/// <summary>How a declared exposure compares with the final identity registrations.</summary>
public enum ExposureState { Resolves, Elsewhere, Twice, Bypass, Kept, Absent }

/// <summary>Registry marker filters. Any selected marker matches a row.</summary>
[Flags]
public enum ServiceMarkers { Standard = 1, Keyed = 2, Multiple = 4, Warnings = 8, Errors = 16 }

/// <summary>A service type and key with its descriptors in collection order.</summary>
public sealed class ServiceIdentity(DependencyInjectionDescriptorInfo[] descriptors)
{
    public DependencyInjectionDescriptorInfo[] Descriptors { get; } = descriptors;
    public DependencyInjectionDescriptorInfo Winner => Descriptors[^1];
    public int Index => Winner.ServiceIdentityIndex;
    public string Type => Winner.ServiceType;
    public string Display => Winner.ServiceTypeDisplayName;
    public string? Key => Winner.ServiceKey;
    public bool Divergent { get; } = descriptors.Select(ImplementationIdentity).Distinct().Skip(1).Any();
    public bool Conventional { get; } = descriptors.Any(d => d.IsAutoRegistered);
    public bool MixedLifetimes { get; } = descriptors.Select(d => d.Lifetime).Distinct().Skip(1).Any();
    public ServiceMarkers Markers { get; } = GetMarkers(descriptors);

    private static (string? Type, string? Assembly, string? Factory) ImplementationIdentity(DependencyInjectionDescriptorInfo d)
        => (d.ImplementationType, d.ImplementationAssemblyName,
            d.Activation == DependencyInjectionActivation.Factory ? d.FactoryDisplay : null);

    private static ServiceMarkers GetMarkers(DependencyInjectionDescriptorInfo[] descriptors)
    {
        var markers = (descriptors[0].IsKeyedService ? ServiceMarkers.Keyed : 0)
            | (descriptors.Length > 1 ? ServiceMarkers.Multiple : 0)
            | (descriptors.Any(d => d.HasWarningIssues) ? ServiceMarkers.Warnings : 0)
            | (descriptors.Any(d => d.HasErrorIssues) ? ServiceMarkers.Errors : 0);
        return markers == 0 ? ServiceMarkers.Standard : markers;
    }
}

/// <summary>A declared exposure and its observed resolution evidence.</summary>
public sealed record Exposure(DependencyInjectionExposedServiceInfo Declared, ServiceIdentity? Identity,
    DependencyInjectionDescriptorInfo? Own, ExposureState State)
{
    public DependencyInjectionDescriptorInfo? Winner => Identity?.Winner;

    public ConventionFinding Finding => State switch
    {
        ExposureState.Elsewhere => ConventionFinding.ResolvesElsewhere,
        ExposureState.Twice => ConventionFinding.RegisteredTwice,
        ExposureState.Bypass => ConventionFinding.BypassesActivation,
        _ => ConventionFinding.Attention
    };
}

/// <summary>A conventional implementation and the evidence for its findings.</summary>
public sealed record ConventionImplementation(
    string Type, string Display, string? Assembly, ServiceLifetime Lifetime,
    DependencyInjectionLifetimeSource LifetimeSource, DependencyInjectionAutoRegistrationMode? Mode,
    DependencyInjectionDescriptorInfo[] Descriptors, Exposure[] Exposures, ConventionFinding Findings)
{
    public bool NeedsAttention => (Findings & ~ConventionFinding.ConcreteOnly) != 0;
    public bool Replaced => Descriptors.Any(d => d.AutoRegistration?.Outcome == DependencyInjectionAutoRegistrationOutcome.ReplacedExisting);
    public bool Forwarded => Descriptors.Any(d => d.Activation == DependencyInjectionActivation.Forward);

    public bool Matches(ConventionFinding filter) => filter == ConventionFinding.Attention
        ? NeedsAttention : (Findings & filter) != 0;

    public bool Matches(string search) => string.IsNullOrWhiteSpace(search) ||
        new[] { Type, Assembly, Lifetime.ToString(), LifetimeSource.ToString() }
            .Concat(Exposures.SelectMany(e => new[] { e.Declared.ServiceType, e.Declared.ServiceKey }))
            .Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
}

/// <summary>The composition and service views derived from one captured diagnostics snapshot.</summary>
public sealed class DependencyInjectionWorkbenchSnapshot
{
    public DependencyInjectionWorkbenchSnapshot(DependencyInjectionDiagnosticsSnapshot raw)
    {
        Raw = raw;
        Descriptors = raw.Descriptors.OrderBy(d => d.Index).ToArray();
        Identities = Descriptors.GroupBy(d => d.ServiceIdentityIndex)
            .ToDictionary(g => g.Key, g => new ServiceIdentity(g.ToArray()));

        var byType = Descriptors.Where(d => d.AutoRegistration is not null)
            .ToLookup(d => d.AutoRegistration!.SourceImplementationType);
        var issues = raw.AutoRegistrationIssues.ToLookup(i => i.SourceImplementationType);
        Implementations = byType.Select(g => g.Key).Union(issues.Select(g => g.Key)).Select(type =>
        {
            var descriptors = byType[type].ToArray();
            var registration = descriptors.FirstOrDefault()?.AutoRegistration;
            var issue = issues[type].FirstOrDefault();
            var monicaActivated = descriptors.Any(d => d.Activation == DependencyInjectionActivation.MonicaActivated);
            var exposures = (registration?.ExposedServices ?? issue!.ExposedServices)
                .Select(e => Resolve(type, registration?.RegistrationMode, monicaActivated, e)).ToArray();
            var findings = exposures.Aggregate(ConventionFinding.Attention, (value, exposure) => value | exposure.Finding);
            foreach (var diagnostic in issues[type])
            {
                findings |= diagnostic.Kind switch
                {
                    DependencyInjectionAutoRegistrationIssueKind.MissingExposedServices => ConventionFinding.NotRegistered,
                    DependencyInjectionAutoRegistrationIssueKind.ConcreteTypeOnlyExposure => ConventionFinding.ConcreteOnly,
                    _ => ConventionFinding.Attention
                };
            }

            return new ConventionImplementation(type,
                registration?.SourceImplementationTypeDisplayName ?? issue!.SourceImplementationTypeDisplayName,
                registration is not null ? registration.SourceImplementationAssemblyName : issue!.SourceImplementationAssemblyName,
                registration?.Lifetime ?? issue!.Lifetime, registration?.LifetimeSource ?? issue!.LifetimeSource,
                registration?.RegistrationMode, descriptors, exposures, findings);
        }).OrderBy(i => i.Display, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public DependencyInjectionDiagnosticsSnapshot Raw { get; }
    public DependencyInjectionDescriptorInfo[] Descriptors { get; }
    public IReadOnlyDictionary<int, ServiceIdentity> Identities { get; }
    public ConventionImplementation[] Implementations { get; }

    private Exposure Resolve(string type, DependencyInjectionAutoRegistrationMode? mode, bool monicaActivated,
        DependencyInjectionExposedServiceInfo declared)
    {
        var identity = declared.ServiceIdentityIndex is { } index ? Identities.GetValueOrDefault(index) : null;
        var own = identity?.Descriptors.FirstOrDefault(d => d.AutoRegistration?.SourceImplementationType == type);
        if (own is null)
        {
            return new(declared, identity, null,
                mode == DependencyInjectionAutoRegistrationMode.TryAdd ? ExposureState.Kept : ExposureState.Absent);
        }

        var winner = identity!.Winner;
        if (winner.AutoRegistration?.SourceImplementationType == type)
        {
            var duplicate = identity.Descriptors.Any(d => d.Index != winner.Index && NamesImplementation(d, type));
            return new(declared, identity, own, duplicate ? ExposureState.Twice : ExposureState.Resolves);
        }

        if (!NamesImplementation(winner, type))
        {
            return new(declared, identity, own, ExposureState.Elsewhere);
        }

        var bypass = winner.ImplementationKind == DependencyInjectionDescriptorImplementationKind.Type
            && (own.Activation == DependencyInjectionActivation.MonicaActivated ||
                own.Activation == DependencyInjectionActivation.Forward && monicaActivated);
        return new(declared, identity, own, bypass ? ExposureState.Bypass : ExposureState.Twice);
    }

    public static bool NamesImplementation(DependencyInjectionDescriptorInfo descriptor, string type)
        => descriptor.ImplementationType == type || descriptor.AutoRegistration?.SourceImplementationType == type;
}
