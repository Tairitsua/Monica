using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.DependencyInjection.Models;
using Monica.Framework.UI.Localization;
using Monica.UI.Shared.Components.Visual;

namespace Monica.Framework.UI.UIDependencyInjection.Models;

internal static class DependencyInjectionPresentation
{
    public static readonly ServiceLifetime[] Lifetimes = [ServiceLifetime.Singleton, ServiceLifetime.Scoped, ServiceLifetime.Transient];
    public static readonly DependencyInjectionDescriptorImplementationKind[] Kinds =
        [DependencyInjectionDescriptorImplementationKind.Type, DependencyInjectionDescriptorImplementationKind.Factory, DependencyInjectionDescriptorImplementationKind.Instance];

    public static readonly ConventionFinding[] Reasons =
        [ConventionFinding.NotRegistered, ConventionFinding.BypassesActivation, ConventionFinding.ResolvesElsewhere, ConventionFinding.RegisteredTwice];

    public static readonly (ConventionFinding Value, string Url)[] FindingFilters =
    [
        (ConventionFinding.Attention, ""),
        (ConventionFinding.NotRegistered, "notRegistered"),
        (ConventionFinding.BypassesActivation, "bypassesActivation"),
        (ConventionFinding.ResolvesElsewhere, "resolvesElsewhere"),
        (ConventionFinding.RegisteredTwice, "registeredTwice"),
        (ConventionFinding.ConcreteOnly, "concreteOnly")
    ];

    public static readonly (ServiceMarkers Value, string Url)[] MarkerFilters =
    [
        (ServiceMarkers.Standard, "standard"),
        (ServiceMarkers.Keyed, "keyed"),
        (ServiceMarkers.Multiple, "multiple"),
        (ServiceMarkers.Warnings, "warnings"),
        (ServiceMarkers.Errors, "errors")
    ];

    public static MoVisualRole FindingRole(ConventionFinding finding) => finding switch
    {
        ConventionFinding.NotRegistered or ConventionFinding.BypassesActivation => MoVisualRole.Error,
        ConventionFinding.ResolvesElsewhere or ConventionFinding.RegisteredTwice => MoVisualRole.Warning,
        ConventionFinding.ConcreteOnly => MoVisualRole.Neutral,
        _ => MoVisualRole.Primary
    };

    public static string CssRole(MoVisualRole role) => role switch
    {
        MoVisualRole.Info => "role-info",
        MoVisualRole.Secondary => "role-third",
        MoVisualRole.Warning => "role-warning",
        MoVisualRole.Error => "role-error",
        MoVisualRole.Success => "role-success",
        MoVisualRole.Primary => "role-primary",
        _ => "role-neutral"
    };

    public static string Lifetime(IStringLocalizer<DependencyInjectionResource> l, ServiceLifetime lifetime) => lifetime switch
    {
        ServiceLifetime.Singleton => l["Workbench:Lifetimes:Singleton"],
        ServiceLifetime.Scoped => l["Workbench:Lifetimes:Scoped"],
        _ => l["Workbench:Lifetimes:Transient"]
    };

    public static string Kind(IStringLocalizer<DependencyInjectionResource> l, DependencyInjectionDescriptorImplementationKind kind) => kind switch
    {
        DependencyInjectionDescriptorImplementationKind.Type => l["Workbench:Kinds:Type"],
        DependencyInjectionDescriptorImplementationKind.Factory => l["Workbench:Kinds:Factory"],
        _ => l["Workbench:Kinds:Instance"]
    };

    public static string Source(IStringLocalizer<DependencyInjectionResource> l, DependencyInjectionLifetimeSource source) => source switch
    {
        DependencyInjectionLifetimeSource.DependencyAttribute => l["Workbench:Sources:DependencyAttribute"],
        DependencyInjectionLifetimeSource.TransientMarkerInterface => l["Workbench:Sources:TransientMarkerInterface"],
        DependencyInjectionLifetimeSource.SingletonMarkerInterface => l["Workbench:Sources:SingletonMarkerInterface"],
        DependencyInjectionLifetimeSource.ScopedMarkerInterface => l["Workbench:Sources:ScopedMarkerInterface"],
        _ => l["Workbench:Sources:Unknown"]
    };

    public static string Reason(IStringLocalizer<DependencyInjectionResource> l, ConventionFinding finding) => finding switch
    {
        ConventionFinding.NotRegistered => l["Workbench:Reasons:NotRegistered"],
        ConventionFinding.BypassesActivation => l["Workbench:Reasons:BypassesActivation"],
        ConventionFinding.ResolvesElsewhere => l["Workbench:Reasons:ResolvesElsewhere"],
        ConventionFinding.RegisteredTwice => l["Workbench:Reasons:RegisteredTwice"],
        _ => l["Workbench:Reasons:ConcreteOnly"]
    };

    public static string ReasonTip(IStringLocalizer<DependencyInjectionResource> l, ConventionFinding reason) => reason switch
    {
        ConventionFinding.NotRegistered => l["Workbench:ReasonTips:NotRegistered"],
        ConventionFinding.BypassesActivation => l["Workbench:ReasonTips:BypassesActivation"],
        ConventionFinding.ResolvesElsewhere => l["Workbench:ReasonTips:ResolvesElsewhere"],
        ConventionFinding.RegisteredTwice => l["Workbench:ReasonTips:RegisteredTwice"],
        _ => l["Workbench:ReasonTips:ConcreteOnly"]
    };

    public static string Activation(IStringLocalizer<DependencyInjectionResource> l, DependencyInjectionActivation activation) => activation switch
    {
        DependencyInjectionActivation.Direct => l["Workbench:Activations:Direct"],
        DependencyInjectionActivation.Forward => l["Workbench:Activations:Forward"],
        DependencyInjectionActivation.MonicaActivated => l["Workbench:Activations:Monica"],
        _ => l["Workbench:Activations:Factory"]
    };

    public static string Implementation(IStringLocalizer<DependencyInjectionResource> l, DependencyInjectionDescriptorInfo d)
    {
        if (d.Activation != DependencyInjectionActivation.Factory ||
            (d.ImplementationTypeDisplayName is not null && d.ImplementationType != d.ServiceType))
        {
            return d.ImplementationTypeDisplayName ?? l["Workbench:UnknownImpl"];
        }

        var signature = d.FactoryDisplay;
        var owner = signature?.Split('+')[0].Split('<')[0].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var name = owner is { Length: > 0 } && signature!.Contains('+') ? owner[^1]
            : owner is { Length: > 1 } ? owner[^2] : null;
        return name is null ? l["Workbench:Kinds:Factory"] : $"{l["Workbench:Kinds:Factory"]} · {name}";
    }
}
