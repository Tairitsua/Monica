using Monica.Core.Execution;
using Monica.Core.Execution.Models;
using Monica.UI.Shared.Components.Visual;

namespace Monica.Framework.UI.UIExecutionPipeline.Models;

/// <summary>Projects the immutable host catalog into the execution-pipeline workbench.</summary>
public sealed class ExecutionPipelineView
{
    internal const string HttpPoint = "webapi.mvc-action";
    private const string UnitOfWorkType = "Monica.Repository.UnitOfWork.Services.Behaviors.UnitOfWorkExecutionBehavior";
    private static readonly PipelineBand[] Bands =
    [
        new("Diagnostics", ExecutionBehaviorOrder.Diagnostics, MoVisualRole.Info),
        new("Authorization", ExecutionBehaviorOrder.Authorization, MoVisualRole.Primary),
        new("Routing", ExecutionBehaviorOrder.Routing, MoVisualRole.Primary),
        new("Validation", ExecutionBehaviorOrder.Validation, MoVisualRole.Primary),
        new("UnitOfWork", ExecutionBehaviorOrder.UnitOfWork, MoVisualRole.Secondary),
        new("Application", ExecutionBehaviorOrder.Application, MoVisualRole.Neutral)
    ];

    internal ExecutionPipelineView(ExecutionPipelineCatalogSnapshot snapshot, ExecutionPipelineCatalogSnapshot? previous)
    {
        Snapshot = snapshot;
        PreviousAt = previous?.CapturedAt;
        var previousIds = previous?.Plans.Select(plan => plan.Id).ToHashSet();
        Plans = snapshot.Plans.Select(plan => new PipelineRow(plan,
                snapshot.CapturedAt - plan.StartedAt, previousIds is not null && !previousIds.Contains(plan.Id)))
            .OrderBy(row => row.Plan.Descriptor.FullName, StringComparer.Ordinal)
            .ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
        FirstAt = Plans.Length == 0 ? null : Plans.Min(row => row.Plan.StartedAt);
        Ready = Plans.Where(row => row.Ready).ToArray();
        Faulted = Plans.Where(row => row.Plan.Status == ExecutionPipelinePlanStatus.Faulted)
            .OrderByDescending(row => row.Plan.StartedAt).ToArray();
        Stalled = Plans.Where(row => row.Stalled).OrderByDescending(row => row.Plan.StartedAt).ToArray();
        Building = Plans.Where(row => row.Plan.Status == ExecutionPipelinePlanStatus.Building && !row.Stalled).ToArray();
        Points = Plans.GroupBy(row => row.Point, StringComparer.Ordinal)
            .Select(group => new PipelineGroup(group.Key, group.ToArray(), group.Count(row => row.Blocking)))
            .OrderByDescending(group => group.Blocking > 0).ThenBy(group => group.Point, StringComparer.Ordinal).ToArray();
        var duplicates = snapshot.Registrations.GroupBy(reg => ShortName(reg.BehaviorType))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var reach = Ready.SelectMany(row => row.Plan.Behaviors)
            .GroupBy(link => link.RegisteredType.Diagnostics.AssemblyQualifiedName)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Registrations = snapshot.Registrations.Select((reg, index) =>
        {
            var name = ShortName(reg.BehaviorType);
            var identity = reg.BehaviorType.Diagnostics.AssemblyQualifiedName;
            var band = Bands.MinBy(band => Math.Abs((long)reg.Order - band.Anchor))!;
            var unitOfWork = reg.BehaviorType.AssemblyName == "Monica.Repository" &&
                             reg.BehaviorType.FullName.Split('<', 2)[0] == UnitOfWorkType;
            var label = name.EndsWith("ExecutionBehavior", StringComparison.Ordinal) ? name[..^17]
                : name.EndsWith("Behavior", StringComparison.Ordinal) ? name[..^8] : name;
            return new PipelineRegistration(reg, index, duplicates.Contains(name) ? identity : name,
                name, label.Length == 0 ? name : label, band, reach.GetValueOrDefault(identity), unitOfWork);
        }).ToArray();
        Guard = Registrations.Where(reg => reg.Band.Key == "Authorization").OrderByDescending(reg => reg.Applied > 0).FirstOrDefault();
    }

    internal ExecutionPipelineCatalogSnapshot Snapshot { get; }
    internal DateTimeOffset? PreviousAt { get; }
    internal DateTimeOffset? FirstAt { get; }
    internal PipelineRow[] Plans { get; }
    internal PipelineRow[] Ready { get; }
    internal PipelineRow[] Faulted { get; }
    internal PipelineRow[] Stalled { get; }
    internal PipelineRow[] Building { get; }
    internal PipelineGroup[] Points { get; }
    internal PipelineRegistration[] Registrations { get; }
    internal PipelineRegistration? Guard { get; }
    internal int Blocking => Faulted.Length + Stalled.Length;

    internal PipelineRegistration? FindRegistration(string? key) =>
        Registrations.FirstOrDefault(reg => reg.Key == key || reg.Identity == key);

    internal PipelineRow[] Filter(ExecutionPipelineQuery query)
    {
        var reg = FindRegistration(query.Behavior);
        return Plans.Where(row =>
        {
            var plan = row.Plan;
            return (query.Point is null || row.Point == query.Point)
                && (query.Status is null || plan.Status.ToString() == query.Status)
                && (query.Tx is null || plan.Descriptor.TransactionMode.ToString() == query.Tx)
                && (query.Business is null || (plan.Descriptor.IsBusinessOperation ? "yes" : "no") == query.Business)
                && (query.Behavior is null || (reg is not null && row.Ready && row.Applies(reg) == (query.Applied != "0")))
                && (string.IsNullOrWhiteSpace(query.Q) || MatchesText(plan, query.Q.Trim()));
        }).ToArray();
    }

    internal PipelineGroup[] Groups(PipelineRow[] rows, bool recent)
    {
        var byPoint = rows.GroupBy(row => row.Point).ToDictionary(group => group.Key, group =>
            group.OrderByDescending(row => row.Blocking)
                .ThenByDescending(row => recent ? row.Plan.StartedAt : DateTimeOffset.MinValue).ToArray());
        return Points.Where(point => byPoint.ContainsKey(point.Point))
            .Select(point => point with { Rows = byPoint[point.Point], Total = point.Rows.Length }).ToArray();
    }

    internal static string ShortName(ExecutionTypeSnapshot type) => type.Name.Split('<', 2)[0];

    private static bool MatchesText(ExecutionPipelinePlanSnapshot plan, string query)
    {
        var descriptor = plan.Descriptor;
        return Contains(plan.Id.Value) || Contains(plan.Diagnostics.CanonicalKey)
            || Contains(descriptor.Id.Value) || Contains(descriptor.Diagnostics.CanonicalKey)
            || Contains(descriptor.FullName) || Contains(descriptor.Point.Value)
            || TypeMatches(descriptor.ComponentType) || TypeMatches(descriptor.ContractType)
            || TypeMatches(descriptor.InputType) || TypeMatches(descriptor.ResultType)
            || Contains(descriptor.EntryMethod?.DisplaySignature)
            || plan.Behaviors.Any(link => TypeMatches(link.RegisteredType) || TypeMatches(link.ResolvedType)
                || Contains(link.SourceModuleKey?.Value));

        bool Contains(string? value) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        bool TypeMatches(ExecutionTypeSnapshot? type) => type is not null &&
            (Contains(type.Name) || Contains(type.FullName) || Contains(type.AssemblyName)
             || Contains(type.Diagnostics.AssemblyQualifiedName));
    }
}

internal sealed record PipelineBand(string Key, int Anchor, MoVisualRole Role);

internal sealed record PipelineRegistration(
    ExecutionBehaviorRegistrationSnapshot Registration, int Index, string Key, string Name, string Label,
    PipelineBand Band, int Applied, bool UnitOfWork)
{
    internal string Identity => Registration.BehaviorType.Diagnostics.AssemblyQualifiedName;
}

/// <summary>Contains the displayed rows and complete capture counts for one execution point.</summary>
public sealed record PipelineGroup(string Point, PipelineRow[] Rows, int Blocking, int Total = 0);

/// <summary>Contains plan evidence and its age and novelty at capture time.</summary>
public sealed record PipelineRow(ExecutionPipelinePlanSnapshot Plan, TimeSpan Age, bool IsNew)
{
    private readonly Dictionary<string, ExecutionPipelineAppliedBehaviorSnapshot> _chain =
        Plan.Behaviors.ToDictionary(link => link.RegisteredType.Diagnostics.AssemblyQualifiedName, StringComparer.Ordinal);

    internal string Id => Plan.Id.Value;
    internal string Point => Plan.Descriptor.Point.Value;
    internal string Name => Plan.Descriptor.Name;
    internal bool Ready => Plan.Status == ExecutionPipelinePlanStatus.Ready;
    internal bool Stalled => Plan.Status == ExecutionPipelinePlanStatus.Building && Age >= TimeSpan.FromSeconds(1);
    internal bool Blocking => Plan.Status == ExecutionPipelinePlanStatus.Faulted || Stalled;
    internal string Handler => Plan.Descriptor.EntryMethod?.Name ?? Point;
    internal bool Applies(PipelineRegistration registration) => _chain.ContainsKey(registration.Identity);
    internal bool Joins(PipelineRegistration registration) =>
        registration.UnitOfWork && Plan.Descriptor.TransactionMode == ExecutionTransactionMode.None;
}

/// <summary>Holds shareable filter, ordering, expansion, and evidence selection for this route.</summary>
public sealed record ExecutionPipelineQuery(
    string? Q = null, string? Point = null, string? Status = null, string? Tx = null, string? Business = null,
    string? Behavior = null, string? Applied = null, string? Sort = null, string? Open = null,
    string? Plan = null, string? Registration = null)
{
    internal bool Filtered => !string.IsNullOrWhiteSpace(Q) || Point is not null || Status is not null
        || Tx is not null || Business is not null || Behavior is not null;
}
