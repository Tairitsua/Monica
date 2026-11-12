using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.BuildingBlocks.Abstractions;
using Monica.BuildingBlocks.Models;
using Monica.Tool.Extensions;

namespace Monica.BuildingBlocks.Services;

internal sealed class BuildingBlockProjectionService(
    IBuildingBlockCatalog catalog,
    IMonicaApplicationOptions applicationOptions,
    IBuildingBlockRequirementLinkResolver requirementLinkResolver,
    ILogger<BuildingBlockProjectionService> logger)
{
    internal List<BuildingBlockSummary> GetAll()
    {
        var units = catalog.GetAll();
        var dependedByCounts = BuildDependedByCounts(units);
        return units
            .Select(unit => CreateSummary(unit, dependedByCounts))
            .OrderBy(unit => unit.Kind)
            .ThenBy(unit => unit.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal BuildingBlockDashboardSnapshot GetDashboard()
    {
        var units = catalog.GetAll();
        var dependedByCounts = BuildDependedByCounts(units);
        var summaries = units.ToDictionary(
            unit => unit.Key,
            unit => CreateSummary(unit, dependedByCounts),
            StringComparer.Ordinal);
        var totalUnits = units.Count;

        return new BuildingBlockDashboardSnapshot
        {
            Service = CreateServiceIdentity(),
            TotalBuildingBlocks = totalUnits,
            DistinctKinds = units.Select(unit => unit.Kind).Distinct().Count(),
            Coverage =
            [
                CreateCoverageMetric(
                    BuildingBlockCoverageKind.Metadata,
                    units.Count(unit => unit.HasExplicitMetadata),
                    totalUnits),
                CreateCoverageMetric(
                    BuildingBlockCoverageKind.Description,
                    units.Count(unit => !string.IsNullOrWhiteSpace(unit.Description)),
                    totalUnits),
                CreateCoverageMetric(
                    BuildingBlockCoverageKind.Ownership,
                    units.Count(unit => !string.IsNullOrWhiteSpace(unit.Owner)),
                    totalUnits),
                CreateCoverageMetric(
                    BuildingBlockCoverageKind.Requirements,
                    units.Count(unit => unit.RequirementIds.Count != 0),
                    totalUnits)
            ],
            Kinds = units
                .GroupBy(unit => unit.Kind)
                .Select(group => new BuildingBlockKindStatistics
                {
                    Kind = group.Key,
                    Count = group.Count(),
                    Percentage = CalculatePercentage(group.Count(), totalUnits) ?? 0m
                })
                .OrderByDescending(statistics => statistics.Count)
                .ThenBy(statistics => statistics.Kind)
                .ToList(),
            Dependencies = new BuildingBlockDependencyStatistics
            {
                EdgeCount = units.Sum(unit => unit.Dependencies.Count),
                IsolatedBuildingBlockCount = units.Count(unit =>
                    unit.Dependencies.Count == 0 && !dependedByCounts.ContainsKey(unit.Key))
            },
            Alerts = CreateAlertStatistics(units),
            CoverageGaps = units
                .Select(unit => CreateCoverageGap(unit, summaries[unit.Key]))
                .Where(gap => gap.MissingCoverage.Count != 0 || gap.BuildingBlock.Alerts.Count != 0)
                .OrderByDescending(gap => gap.BuildingBlock.Alerts.Any(alert => alert.Level == EAlertLevel.Error))
                .ThenByDescending(gap => gap.BuildingBlock.Alerts.Any(alert => alert.Level == EAlertLevel.Warning))
                .ThenByDescending(gap => gap.MissingCoverage.Count)
                .ThenBy(gap => gap.BuildingBlock.Title, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    internal async Task<BuildingBlockDetail> GetDetailAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (catalog.FindByFullName(key) is not { } unit)
        {
            throw new KeyNotFoundException($"Building block '{key}' was not found in the current host's catalog.");
        }

        var units = catalog.GetAll();
        var dependedByCounts = BuildDependedByCounts(units);

        return new BuildingBlockDetail
        {
            Summary = CreateSummary(unit, dependedByCounts),
            Dependents = units
                .Where(candidate => candidate.Dependencies.Contains(unit))
                .Select(CreateReference)
                .OrderBy(reference => reference.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Requirements = await ResolveRequirementsAsync(unit.RequirementIds, cancellationToken),
            Methods = unit.Methods
                .Select(method => new BuildingBlockMethodSummary
                {
                    Name = method.MethodName,
                    Signature = method.MethodSignature,
                    Description = Normalize(method.Description),
                    IsStatic = method.MethodInfo.IsStatic,
                    IsVirtual = method.MethodInfo.IsVirtual,
                    IsAbstract = method.MethodInfo.IsAbstract,
                    IsAsync = IsTaskLike(method.MethodInfo.ReturnType)
                })
                .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(method => method.Signature, StringComparer.Ordinal)
                .ToList(),
            ConstructorParameters = unit.ConstructorParameterTypes
                .Select(parameterType => parameterType.GetCleanName())
                .ToList(),
            Configuration = CreateConfigurationDetail(unit)
        };
    }

    internal List<BuildingBlockDomainEventInfo> GetDomainEvents()
    {
        var units = catalog.GetAll();
        var dependedByCounts = BuildDependedByCounts(units);

        return catalog.GetAll<DomainEventBuildingBlock>()
            .Select(unit => new BuildingBlockDomainEventInfo
            {
                EventKey = unit.Key,
                Info = CreateSummary(unit, dependedByCounts),
                Structure = unit.GetStructure()
            })
            .ToList();
    }

    private BuildingBlockServiceIdentity CreateServiceIdentity()
    {
        var projectName = Normalize(applicationOptions.ProjectName) ?? "Unknown";
        return new BuildingBlockServiceIdentity
        {
            ProjectName = projectName,
            AppId = Normalize(applicationOptions.AppId) ?? projectName,
            AppName = Normalize(applicationOptions.AppName) ?? projectName,
            AppVersion = Normalize(applicationOptions.AppVersion),
            DomainName = Normalize(applicationOptions.DomainName)
        };
    }

    private async Task<List<BuildingBlockRequirementReference>> ResolveRequirementsAsync(
        IReadOnlyList<string> requirementIds,
        CancellationToken cancellationToken)
    {
        var references = new List<BuildingBlockRequirementReference>(requirementIds.Count);
        foreach (var requirementId in requirementIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildingBlockRequirementLink? link = null;

            try
            {
                link = await requirementLinkResolver.ResolveAsync(requirementId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to resolve requirement {RequirementId} for building-block detail navigation.",
                    requirementId);
            }

            var safeHref = NormalizeSafeHref(link?.Href);
            if (link is not null && safeHref is null)
            {
                logger.LogWarning(
                    "Requirement resolver returned an unsafe or invalid link for {RequirementId}: {RequirementHref}",
                    requirementId,
                    link.Href);
            }

            references.Add(new BuildingBlockRequirementReference
            {
                Id = requirementId,
                Title = Normalize(link?.Title) ?? requirementId,
                Href = safeHref
            });
        }

        return references;
    }

    private static BuildingBlockSummary CreateSummary(
        BuildingBlock unit,
        IReadOnlyDictionary<string, int> dependedByCounts)
    {
        return new BuildingBlockSummary
        {
            Key = unit.Key,
            Title = unit.Title,
            Description = Normalize(unit.Description),
            Owner = Normalize(unit.Owner),
            Tags = unit.Tags.ToList(),
            Kind = unit.Kind,
            ExecutionPoints = unit.ExecutionPoints.ToList(),
            HasExplicitMetadata = unit.HasExplicitMetadata,
            RequirementCount = unit.RequirementIds.Count,
            MethodCount = unit.Methods.Count,
            Dependencies = unit.Dependencies
                .Select(CreateReference)
                .OrderBy(reference => reference.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DependedByCount = dependedByCounts.GetValueOrDefault(unit.Key),
            Alerts = unit.Alerts.Select(CloneAlert).ToList()
        };
    }

    private static BuildingBlockReference CreateReference(BuildingBlock unit)
    {
        return new BuildingBlockReference
        {
            Key = unit.Key,
            Title = unit.Title,
            Kind = unit.Kind
        };
    }

    private static BuildingBlockAlert CloneAlert(BuildingBlockAlert alert)
    {
        return new BuildingBlockAlert
        {
            Level = alert.Level,
            Message = alert.Message,
            Source = alert.Source,
            CreatedAt = alert.CreatedAt
        };
    }

    private static Dictionary<string, int> BuildDependedByCounts(IReadOnlyList<BuildingBlock> units)
    {
        return units
            .SelectMany(unit => unit.Dependencies)
            .GroupBy(dependency => dependency.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    private static BuildingBlockCoverageMetric CreateCoverageMetric(
        BuildingBlockCoverageKind kind,
        int covered,
        int total)
    {
        return new BuildingBlockCoverageMetric
        {
            Kind = kind,
            Covered = covered,
            Total = total,
            Percentage = CalculatePercentage(covered, total)
        };
    }

    private static decimal? CalculatePercentage(int count, int total)
    {
        return total == 0 ? null : Math.Round(count * 100m / total, 1, MidpointRounding.AwayFromZero);
    }

    private static BuildingBlockCoverageGap CreateCoverageGap(BuildingBlock unit, BuildingBlockSummary summary)
    {
        var missingCoverage = new List<BuildingBlockCoverageKind>();
        if (!unit.HasExplicitMetadata)
        {
            missingCoverage.Add(BuildingBlockCoverageKind.Metadata);
        }

        if (string.IsNullOrWhiteSpace(unit.Description))
        {
            missingCoverage.Add(BuildingBlockCoverageKind.Description);
        }

        if (string.IsNullOrWhiteSpace(unit.Owner))
        {
            missingCoverage.Add(BuildingBlockCoverageKind.Ownership);
        }

        if (unit.RequirementIds.Count == 0)
        {
            missingCoverage.Add(BuildingBlockCoverageKind.Requirements);
        }

        return new BuildingBlockCoverageGap
        {
            BuildingBlock = summary,
            MissingCoverage = missingCoverage
        };
    }

    private static BuildingBlockAlertStatistics CreateAlertStatistics(IReadOnlyList<BuildingBlock> units)
    {
        var alerts = units.SelectMany(unit => unit.Alerts).ToList();
        return new BuildingBlockAlertStatistics
        {
            InformationCount = alerts.Count(alert => alert.Level == EAlertLevel.Info),
            WarningCount = alerts.Count(alert => alert.Level == EAlertLevel.Warning),
            ErrorCount = alerts.Count(alert => alert.Level == EAlertLevel.Error)
        };
    }

    private static BuildingBlockConfigurationDetail? CreateConfigurationDetail(BuildingBlock unit)
    {
        if (unit is not ConfigurationBuildingBlock configuration)
        {
            return null;
        }

        return new BuildingBlockConfigurationDetail
        {
            DefinitionKey = configuration.DefinitionKey,
            IsOffline = configuration.IsOffline,
            ReloadBehavior = configuration.InferredReloadBehavior,
            Usages = configuration.ConfigurationDependencies
                .Select(item => new BuildingBlockConfigurationUsage
                {
                    BuildingBlock = CreateReference(item.Key),
                    UsageType = item.Value
                })
                .OrderBy(usage => usage.BuildingBlock.Title, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static bool IsTaskLike(Type returnType)
    {
        if (typeof(Task).IsAssignableFrom(returnType) || returnType == typeof(ValueTask))
        {
            return true;
        }

        return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>);
    }

    private static string? NormalizeSafeHref(string? href)
    {
        if (Normalize(href) is not { } normalized
            || !Uri.TryCreate(normalized, UriKind.RelativeOrAbsolute, out var uri))
        {
            return null;
        }

        if (!uri.IsAbsoluteUri)
        {
            return normalized.StartsWith("//", StringComparison.Ordinal) ? null : normalized;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : null;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
