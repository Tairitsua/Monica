using System.Text.Json.Serialization;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Identifies the Monica host represented by a building-block dashboard snapshot.
/// </summary>
public sealed class BuildingBlockServiceIdentity
{
    /// <summary>
    /// Gets the application project name.
    /// </summary>
    public required string ProjectName { get; init; }

    /// <summary>
    /// Gets the application identifier.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// Gets the application display name.
    /// </summary>
    public required string AppName { get; init; }

    /// <summary>
    /// Gets the application version when configured.
    /// </summary>
    public string? AppVersion { get; init; }

    /// <summary>
    /// Gets the application domain or subdomain name when configured.
    /// </summary>
    public string? DomainName { get; init; }
}

/// <summary>
/// Identifies an independently measured building-block context dimension.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildingBlockCoverageKind
{
    /// <summary>
    /// Explicit <see cref="Annotations.BuildingBlockMetadataAttribute"/> presence.
    /// </summary>
    Metadata,

    /// <summary>
    /// Effective metadata, configuration, or XML documentation description presence.
    /// </summary>
    Description,

    /// <summary>
    /// Explicit metadata owner presence.
    /// </summary>
    Ownership,

    /// <summary>
    /// At least one normalized requirement annotation.
    /// </summary>
    Requirements
}

/// <summary>
/// Reports independent coverage for one building-block context dimension.
/// </summary>
public sealed class BuildingBlockCoverageMetric
{
    /// <summary>
    /// Gets the measured context dimension.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BuildingBlockCoverageKind Kind { get; init; }

    /// <summary>
    /// Gets the number of building blocks satisfying this dimension.
    /// </summary>
    public int Covered { get; init; }

    /// <summary>
    /// Gets the total discovered building block count used as the denominator.
    /// </summary>
    public int Total { get; init; }

    /// <summary>
    /// Gets the coverage percentage, or <see langword="null"/> when the catalog is empty.
    /// </summary>
    public decimal? Percentage { get; init; }
}

/// <summary>
/// Reports the distribution of one architectural building-block category.
/// </summary>
public sealed class BuildingBlockKindStatistics
{
    /// <summary>
    /// Gets the architectural category.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BuildingBlockKind Kind { get; init; }

    /// <summary>
    /// Gets the number of building blocks in this category.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// Gets this category's percentage of the discovered catalog.
    /// </summary>
    public decimal Percentage { get; init; }
}

/// <summary>
/// Summarizes catalog dependency topology.
/// </summary>
public sealed class BuildingBlockDependencyStatistics
{
    /// <summary>
    /// Gets the total number of directed dependency edges.
    /// </summary>
    public int EdgeCount { get; init; }

    /// <summary>
    /// Gets the number of building blocks with neither incoming nor outgoing dependencies.
    /// </summary>
    public int IsolatedBuildingBlockCount { get; init; }
}

/// <summary>
/// Summarizes catalog diagnostics by severity.
/// </summary>
public sealed class BuildingBlockAlertStatistics
{
    /// <summary>
    /// Gets informational alert count.
    /// </summary>
    public int InformationCount { get; init; }

    /// <summary>
    /// Gets warning alert count.
    /// </summary>
    public int WarningCount { get; init; }

    /// <summary>
    /// Gets error alert count.
    /// </summary>
    public int ErrorCount { get; init; }

    /// <summary>
    /// Gets total alert count.
    /// </summary>
    public int TotalCount => InformationCount + WarningCount + ErrorCount;
}

/// <summary>
/// Identifies context debt or catalog diagnostics for one actionable building block.
/// </summary>
public sealed class BuildingBlockCoverageGap
{
    /// <summary>
    /// Gets the affected building block summary.
    /// </summary>
    public required BuildingBlockSummary BuildingBlock { get; init; }

    /// <summary>
    /// Gets context dimensions that are not covered for this building block.
    /// </summary>
    public IReadOnlyList<BuildingBlockCoverageKind> MissingCoverage { get; init; } = [];
}

/// <summary>
/// Provides one startup-stable status snapshot for the current Monica host's building-block catalog.
/// </summary>
public sealed class BuildingBlockDashboardSnapshot
{
    /// <summary>
    /// Gets the represented service identity.
    /// </summary>
    public required BuildingBlockServiceIdentity Service { get; init; }

    /// <summary>
    /// Gets the total number of discovered building blocks.
    /// </summary>
    public int TotalBuildingBlocks { get; init; }

    /// <summary>
    /// Gets the number of distinct building-block categories present in the catalog.
    /// </summary>
    public int DistinctKinds { get; init; }

    /// <summary>
    /// Gets independent context coverage metrics.
    /// </summary>
    public IReadOnlyList<BuildingBlockCoverageMetric> Coverage { get; init; } = [];

    /// <summary>
    /// Gets building block counts grouped by architectural category.
    /// </summary>
    public IReadOnlyList<BuildingBlockKindStatistics> Kinds { get; init; } = [];

    /// <summary>
    /// Gets dependency topology statistics.
    /// </summary>
    public required BuildingBlockDependencyStatistics Dependencies { get; init; }

    /// <summary>
    /// Gets catalog alert statistics.
    /// </summary>
    public required BuildingBlockAlertStatistics Alerts { get; init; }

    /// <summary>
    /// Gets building blocks with missing context or explicit catalog diagnostics.
    /// </summary>
    public IReadOnlyList<BuildingBlockCoverageGap> CoverageGaps { get; init; } = [];
}
