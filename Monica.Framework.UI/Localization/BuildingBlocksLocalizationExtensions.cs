using Microsoft.Extensions.Localization;
using Monica.BuildingBlocks.Models;
using Monica.Framework.UI.UIBuildingBlocks.Models;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.Framework.UI.Localization;

public enum BuildingBlocksCountText
{
    Assembly,
    Enum,
    EnumValue,
    BuildingBlock,
    Dependency,
    DependedBy,
    Requirement,
    Method,
    Error,
    Warning,
    Info,
    Match
}

public enum BuildingBlocksMethodModifier
{
    Static,
    Virtual,
    Abstract,
    Async
}

public static class BuildingBlocksLocalizationExtensions
{
    public static string GetBuildingBlockKindText(this IStringLocalizer<BuildingBlocksResource> localizer, BuildingBlockKind kind)
    {
        return kind switch
        {
            BuildingBlockKind.ApplicationRequestHandler => localizer["Shared:BuildingBlockKinds:ApplicationRequestHandler"].Value,
            BuildingBlockKind.CrudApplicationService => localizer["Shared:BuildingBlockKinds:CrudApplicationService"].Value,
            BuildingBlockKind.DomainService => localizer["Shared:BuildingBlockKinds:DomainService"].Value,
            BuildingBlockKind.Repository => localizer["Shared:BuildingBlockKinds:Repository"].Value,
            BuildingBlockKind.DomainEvent => localizer["Shared:BuildingBlockKinds:DomainEvent"].Value,
            BuildingBlockKind.DistributedEventHandler => localizer["Shared:BuildingBlockKinds:DistributedEventHandler"].Value,
            BuildingBlockKind.LocalEventHandler => localizer["Shared:BuildingBlockKinds:LocalEventHandler"].Value,
            BuildingBlockKind.Seeder => localizer["Shared:BuildingBlockKinds:Seeder"].Value,
            BuildingBlockKind.RecurringJob => localizer["Shared:BuildingBlockKinds:RecurringJob"].Value,
            BuildingBlockKind.TriggeredJob => localizer["Shared:BuildingBlockKinds:TriggeredJob"].Value,
            BuildingBlockKind.HostedService => localizer["Shared:BuildingBlockKinds:HostedService"].Value,
            BuildingBlockKind.HttpApi => localizer["Shared:BuildingBlockKinds:HttpApi"].Value,
            BuildingBlockKind.GrpcApi => localizer["Shared:BuildingBlockKinds:GrpcApi"].Value,
            BuildingBlockKind.StateStore => localizer["Shared:BuildingBlockKinds:StateStore"].Value,
            BuildingBlockKind.EventBus => localizer["Shared:BuildingBlockKinds:EventBus"].Value,
            BuildingBlockKind.Actor => localizer["Shared:BuildingBlockKinds:Actor"].Value,
            BuildingBlockKind.Entity => localizer["Shared:BuildingBlockKinds:Entity"].Value,
            BuildingBlockKind.RequestDto => localizer["Shared:BuildingBlockKinds:RequestDto"].Value,
            BuildingBlockKind.Configuration => localizer["Shared:BuildingBlockKinds:Configuration"].Value,
            _ => kind.ToString()
        };
    }

    public static string GetCountText(this IStringLocalizer<BuildingBlocksResource> localizer, BuildingBlocksCountText countText, int count)
    {
        var singular = count == 1;

        return countText switch
        {
            BuildingBlocksCountText.Assembly => singular
                ? localizer["Shared:Counts:Assembly:Singular", count].Value
                : localizer["Shared:Counts:Assembly:Plural", count].Value,
            BuildingBlocksCountText.Enum => singular
                ? localizer["Shared:Counts:Enum:Singular", count].Value
                : localizer["Shared:Counts:Enum:Plural", count].Value,
            BuildingBlocksCountText.EnumValue => singular
                ? localizer["Shared:Counts:EnumValue:Singular", count].Value
                : localizer["Shared:Counts:EnumValue:Plural", count].Value,
            BuildingBlocksCountText.BuildingBlock => singular
                ? localizer["Shared:Counts:BuildingBlock:Singular", count].Value
                : localizer["Shared:Counts:BuildingBlock:Plural", count].Value,
            BuildingBlocksCountText.Dependency => singular
                ? localizer["Shared:Counts:Dependency:Singular", count].Value
                : localizer["Shared:Counts:Dependency:Plural", count].Value,
            BuildingBlocksCountText.DependedBy => singular
                ? localizer["Shared:Counts:DependedBy:Singular", count].Value
                : localizer["Shared:Counts:DependedBy:Plural", count].Value,
            BuildingBlocksCountText.Requirement => singular
                ? localizer["Shared:Counts:Requirement:Singular", count].Value
                : localizer["Shared:Counts:Requirement:Plural", count].Value,
            BuildingBlocksCountText.Method => singular
                ? localizer["Shared:Counts:Method:Singular", count].Value
                : localizer["Shared:Counts:Method:Plural", count].Value,
            BuildingBlocksCountText.Error => singular
                ? localizer["Shared:Counts:Error:Singular", count].Value
                : localizer["Shared:Counts:Error:Plural", count].Value,
            BuildingBlocksCountText.Warning => singular
                ? localizer["Shared:Counts:Warning:Singular", count].Value
                : localizer["Shared:Counts:Warning:Plural", count].Value,
            BuildingBlocksCountText.Info => singular
                ? localizer["Shared:Counts:Info:Singular", count].Value
                : localizer["Shared:Counts:Info:Plural", count].Value,
            BuildingBlocksCountText.Match => singular
                ? localizer["Shared:Counts:Match:Singular", count].Value
                : localizer["Shared:Counts:Match:Plural", count].Value,
            _ => count.ToString()
        };
    }

    public static string GetAlertLevelText(this IStringLocalizer<BuildingBlocksResource> localizer, EAlertLevel alertLevel)
    {
        return alertLevel switch
        {
            EAlertLevel.Info => localizer["Shared:AlertLevels:Info"].Value,
            EAlertLevel.Warning => localizer["Shared:AlertLevels:Warning"].Value,
            EAlertLevel.Error => localizer["Shared:AlertLevels:Error"].Value,
            _ => alertLevel.ToString()
        };
    }

    public static string GetConfigurationModeText(this IStringLocalizer<BuildingBlocksResource> localizer, bool? isOffline)
    {
        return isOffline switch
        {
            true => localizer["Shared:ConfigurationModes:Offline"].Value,
            false => localizer["Shared:ConfigurationModes:Online"].Value,
            null => localizer["Shared:ConfigurationModes:Unknown"].Value
        };
    }

    public static string GetConfigurationUsageText(this IStringLocalizer<BuildingBlocksResource> localizer, EConfigurationUsageType usage)
    {
        return usage switch
        {
            EConfigurationUsageType.Offline => localizer["Shared:ConfigurationUsageTypes:Offline"].Value,
            EConfigurationUsageType.OnlineSnapshot => localizer["Shared:ConfigurationUsageTypes:OnlineSnapshot"].Value,
            EConfigurationUsageType.OnlineMonitor => localizer["Shared:ConfigurationUsageTypes:OnlineMonitor"].Value,
            EConfigurationUsageType.Unknown => localizer["Shared:ConfigurationUsageTypes:Unknown"].Value,
            _ => localizer["Shared:ConfigurationUsageTypes:Unknown"].Value
        };
    }

    public static string GetMethodModifierText(this IStringLocalizer<BuildingBlocksResource> localizer, BuildingBlocksMethodModifier modifier)
    {
        return modifier switch
        {
            BuildingBlocksMethodModifier.Static => localizer["Shared:MethodModifiers:Static"].Value,
            BuildingBlocksMethodModifier.Virtual => localizer["Shared:MethodModifiers:Virtual"].Value,
            BuildingBlocksMethodModifier.Abstract => localizer["Shared:MethodModifiers:Abstract"].Value,
            BuildingBlocksMethodModifier.Async => localizer["Shared:MethodModifiers:Async"].Value,
            _ => modifier.ToString()
        };
    }

    public static string BuildBuildingBlockGraphTooltip(this IStringLocalizer<BuildingBlocksResource> localizer, BuildingBlockSummary unit)
    {
        var lines = new[]
        {
            unit.Title,
            localizer["BuildingBlockVisualization:Graph:Tooltip:Type", localizer.GetBuildingBlockKindText(unit.Kind)].Value,
            localizer["BuildingBlockVisualization:Graph:Tooltip:Dependencies", unit.Dependencies.Count].Value,
            localizer["BuildingBlockVisualization:Graph:Tooltip:DependedBy", unit.DependedByCount].Value
        };

        return string.Join(Environment.NewLine, lines);
    }

    public static string BuildBuildingBlockGraphSummary(this IStringLocalizer<BuildingBlocksResource> localizer, BuildingBlockSummary unit)
    {
        var parts = new List<string>();

        if (unit.Dependencies.Count > 0)
        {
            parts.Add(localizer["BuildingBlockVisualization:Graph:Summary:Dependencies", unit.Dependencies.Count].Value);
        }

        if (unit.DependedByCount > 0)
        {
            parts.Add(localizer["BuildingBlockVisualization:Graph:Summary:DependedBy", unit.DependedByCount].Value);
        }

        return string.Join(" | ", parts);
    }

    public static object[] BuildBuildingBlockGraphChips(this IStringLocalizer<BuildingBlocksResource> localizer, BuildingBlockSummary unit)
    {
        var chips = new List<object>
        {
            new
            {
                text = localizer.GetBuildingBlockKindText(unit.Kind),
                color = BuildingBlockVisualizationConfig.GetKindColorRole(unit.Kind),
                icon = string.Empty
            }
        };

        if (!string.IsNullOrWhiteSpace(unit.Owner))
        {
            chips.Add(new
            {
                text = unit.Owner,
                color = "dark",
                icon = MoIcons.User
            });
        }

        if (unit.Dependencies.Count > 0)
        {
            chips.Add(new
            {
                text = localizer.GetCountText(BuildingBlocksCountText.Dependency, unit.Dependencies.Count),
                color = "info",
                icon = MoIcons.Link
            });
        }

        if (unit.DependedByCount > 0)
        {
            chips.Add(new
            {
                text = localizer.GetCountText(BuildingBlocksCountText.DependedBy, unit.DependedByCount),
                color = "success",
                icon = MoIcons.ArrowDownLeft
            });
        }

        if (unit.RequirementCount > 0)
        {
            chips.Add(new
            {
                text = localizer.GetCountText(BuildingBlocksCountText.Requirement, unit.RequirementCount),
                color = "secondary",
                icon = MoIcons.ClipboardCheck
            });
        }

        if (unit.MethodCount > 0)
        {
            chips.Add(new
            {
                text = localizer.GetCountText(BuildingBlocksCountText.Method, unit.MethodCount),
                color = "primary",
                icon = MoIcons.Sigma
            });
        }

        if (unit.Alerts.Any())
        {
            var errorCount = unit.Alerts.Count(alert => alert.Level == EAlertLevel.Error);
            var warningCount = unit.Alerts.Count(alert => alert.Level == EAlertLevel.Warning);
            var infoCount = unit.Alerts.Count(alert => alert.Level == EAlertLevel.Info);

            if (errorCount > 0)
            {
                chips.Add(new
                {
                    text = localizer.GetCountText(BuildingBlocksCountText.Error, errorCount),
                    color = "error",
                    icon = MoIcons.Error
                });
            }

            if (warningCount > 0)
            {
                chips.Add(new
                {
                    text = localizer.GetCountText(BuildingBlocksCountText.Warning, warningCount),
                    color = "warning",
                    icon = MoIcons.Warning
                });
            }

            if (infoCount > 0)
            {
                chips.Add(new
                {
                    text = localizer.GetCountText(BuildingBlocksCountText.Info, infoCount),
                    color = "info",
                    icon = MoIcons.Info
                });
            }
        }

        return chips.ToArray();
    }

    public static object[] BuildBuildingBlockGraphMetadata(this IStringLocalizer<BuildingBlocksResource> localizer, BuildingBlockSummary unit)
    {
        var metadata = new List<object>();

        if (unit.Tags.Count != 0)
        {
            metadata.Add(new
            {
                kind = "tags",
                key = localizer["BuildingBlockVisualization:Graph:MetadataLabels:Tags"].Value,
                value = string.Join(", ", unit.Tags)
            });
        }

        if (!string.IsNullOrWhiteSpace(unit.Description))
        {
            metadata.Add(new
            {
                kind = "description",
                key = localizer["BuildingBlockVisualization:Graph:MetadataLabels:Description"].Value,
                value = unit.Description
            });
        }

        if (!string.IsNullOrWhiteSpace(unit.Owner))
        {
            metadata.Add(new
            {
                kind = "owner",
                key = localizer["BuildingBlockVisualization:Graph:MetadataLabels:Owner"].Value,
                value = unit.Owner
            });
        }

        if (unit.RequirementCount > 0)
        {
            metadata.Add(new
            {
                kind = "requirement-count",
                key = localizer["BuildingBlockVisualization:Graph:MetadataLabels:RequirementCount"].Value,
                value = unit.RequirementCount.ToString()
            });
        }

        if (unit.Dependencies.Count > 0)
        {
            metadata.Add(new
            {
                kind = "dependency-count",
                key = localizer["BuildingBlockVisualization:Graph:MetadataLabels:DependencyCount"].Value,
                value = unit.Dependencies.Count.ToString()
            });
        }

        if (unit.MethodCount > 0)
        {
            metadata.Add(new
            {
                kind = "method-count",
                key = localizer["BuildingBlockVisualization:Graph:MetadataLabels:MethodCount"].Value,
                value = unit.MethodCount.ToString()
            });
        }

        return metadata.ToArray();
    }
}
