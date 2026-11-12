using Monica.BuildingBlocks.Models;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.Framework.UI.UIBuildingBlocks.Models;

public static class BuildingBlockVisualizationConfig
{
    public static Color GetKindMudColor(BuildingBlockKind kind)
    {
        return kind switch
        {
            BuildingBlockKind.ApplicationRequestHandler => Color.Primary,
            BuildingBlockKind.CrudApplicationService => Color.Primary,
            BuildingBlockKind.DomainService => Color.Success,
            BuildingBlockKind.Repository => Color.Warning,
            BuildingBlockKind.DomainEvent => Color.Secondary,
            BuildingBlockKind.DistributedEventHandler => Color.Tertiary,
            BuildingBlockKind.LocalEventHandler => Color.Info,
            BuildingBlockKind.RecurringJob => Color.Info,
            BuildingBlockKind.TriggeredJob => Color.Success,
            BuildingBlockKind.HostedService => Color.Info,
            BuildingBlockKind.HttpApi => Color.Error,
            BuildingBlockKind.GrpcApi => Color.Secondary,
            BuildingBlockKind.Entity => Color.Dark,
            BuildingBlockKind.RequestDto => Color.Default,
            BuildingBlockKind.Configuration => Color.Warning,
            BuildingBlockKind.Seeder => Color.Success,
            BuildingBlockKind.StateStore => Color.Error,
            BuildingBlockKind.EventBus => Color.Info,
            BuildingBlockKind.Actor => Color.Tertiary,
            _ => Color.Default
        };
    }

    public static bool IsComplexKind(BuildingBlockKind kind)
    {
        return kind is BuildingBlockKind.ApplicationRequestHandler
            or BuildingBlockKind.CrudApplicationService
            or BuildingBlockKind.DomainService
            or BuildingBlockKind.RecurringJob
            or BuildingBlockKind.TriggeredJob
            or BuildingBlockKind.HostedService
            or BuildingBlockKind.HttpApi
            or BuildingBlockKind.GrpcApi;
    }

    public static string GetKindColorRole(BuildingBlockKind kind)
    {
        return kind switch
        {
            BuildingBlockKind.ApplicationRequestHandler => "primary",
            BuildingBlockKind.CrudApplicationService => "primary",
            BuildingBlockKind.DomainService => "success",
            BuildingBlockKind.Repository => "warning",
            BuildingBlockKind.DomainEvent => "secondary",
            BuildingBlockKind.DistributedEventHandler => "tertiary",
            BuildingBlockKind.LocalEventHandler => "info",
            BuildingBlockKind.RecurringJob => "info",
            BuildingBlockKind.TriggeredJob => "success",
            BuildingBlockKind.HostedService => "info",
            BuildingBlockKind.HttpApi => "error",
            BuildingBlockKind.GrpcApi => "secondary",
            BuildingBlockKind.Entity => "dark",
            BuildingBlockKind.RequestDto => "default",
            BuildingBlockKind.Configuration => "warning",
            BuildingBlockKind.Seeder => "success",
            BuildingBlockKind.StateStore => "error",
            BuildingBlockKind.EventBus => "info",
            BuildingBlockKind.Actor => "tertiary",
            _ => "default"
        };
    }

    public static string GetKindIcon(BuildingBlockKind kind)
    {
        return kind switch
        {
            BuildingBlockKind.ApplicationRequestHandler => MoIcons.Briefcase,
            BuildingBlockKind.CrudApplicationService => MoIcons.NotebookEdit,
            BuildingBlockKind.DomainService => MoIcons.Building,
            BuildingBlockKind.Repository => MoIcons.Database,
            BuildingBlockKind.DomainEvent => MoIcons.Calendar,
            BuildingBlockKind.DistributedEventHandler => MoIcons.CalendarCheck,
            BuildingBlockKind.LocalEventHandler => MoIcons.CalendarDays,
            BuildingBlockKind.RecurringJob => MoIcons.History,
            BuildingBlockKind.TriggeredJob => MoIcons.Clock,
            BuildingBlockKind.HostedService => MoIcons.HardHat,
            BuildingBlockKind.HttpApi => MoIcons.Globe,
            BuildingBlockKind.GrpcApi => MoIcons.Plug,
            BuildingBlockKind.Entity => MoIcons.Table,
            BuildingBlockKind.RequestDto => MoIcons.Braces,
            BuildingBlockKind.Configuration => MoIcons.Settings,
            BuildingBlockKind.Seeder => MoIcons.Sprout,
            BuildingBlockKind.StateStore => MoIcons.Cpu,
            BuildingBlockKind.EventBus => MoIcons.Network,
            BuildingBlockKind.Actor => MoIcons.User,
            _ => MoIcons.Circle
        };
    }

    public static string? GetHighestAlertLevel(BuildingBlockSummary unit)
    {
        if (!unit.Alerts.Any())
        {
            return null;
        }

        if (unit.Alerts.Any(alert => alert.Level == EAlertLevel.Error))
        {
            return "error";
        }

        if (unit.Alerts.Any(alert => alert.Level == EAlertLevel.Warning))
        {
            return "warning";
        }

        return unit.Alerts.Any(alert => alert.Level == EAlertLevel.Info)
            ? "info"
            : null;
    }
}
