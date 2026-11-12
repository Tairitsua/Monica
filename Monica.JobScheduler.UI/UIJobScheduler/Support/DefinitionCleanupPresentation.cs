using Microsoft.Extensions.Localization;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.UI.Localization;
using Monica.UI.Shared.Components.Visual;

namespace Monica.JobScheduler.UI.UIJobScheduler.Support;

internal static class DefinitionCleanupPresentation
{
    internal static string Icon(JobDefinitionCleanupState state) => state switch
    {
        JobDefinitionCleanupState.Ready => Monica.UI.Icons.MoIcons.Check,
        JobDefinitionCleanupState.Queued => Monica.UI.Icons.MoIcons.Hourglass,
        JobDefinitionCleanupState.Running => Monica.UI.Icons.MoIcons.HeartPulse,
        JobDefinitionCleanupState.Published => Monica.UI.Icons.MoIcons.ShieldCheck,
        _ => Monica.UI.Icons.MoIcons.Info
    };

    internal static string Declaration(IStringLocalizer<JobSchedulerResource> localizer, JobDefinitionCleanupItem item) =>
        localizer[item.ManifestHash is null ? "Cleanup:Unknown"
            : item.Definition?.IsPresent == false ? "Cleanup:Unpublished" : "Cleanup:States:Published"];

    internal static MoVisualRole DeclarationRole(JobDefinitionCleanupItem item) => item.ManifestHash is null
        ? MoVisualRole.Neutral : item.Definition?.IsPresent == false ? MoVisualRole.Success
        : item.State == JobDefinitionCleanupState.OwnerUnconfirmed ? MoVisualRole.Neutral : MoVisualRole.Primary;
    internal static readonly JobDefinitionCleanupState[] States = [JobDefinitionCleanupState.Ready,
        JobDefinitionCleanupState.Queued, JobDefinitionCleanupState.Running, JobDefinitionCleanupState.OwnerUnconfirmed];

    internal static string Label(IStringLocalizer<JobSchedulerResource> localizer, JobDefinitionCleanupState state) => state switch
    {
        JobDefinitionCleanupState.Ready => localizer["Cleanup:States:Ready"],
        JobDefinitionCleanupState.Queued => localizer["Cleanup:States:Queued"],
        JobDefinitionCleanupState.Running => localizer["Cleanup:States:Running"],
        JobDefinitionCleanupState.OwnerUnconfirmed => localizer["Cleanup:States:OwnerUnconfirmed"],
        JobDefinitionCleanupState.Published => localizer["Cleanup:States:Published"],
        _ => localizer["Cleanup:States:Missing"]
    };

    internal static string Reason(IStringLocalizer<JobSchedulerResource> localizer, JobDefinitionCleanupState state) => state switch
    {
        JobDefinitionCleanupState.Ready => localizer["Cleanup:Reasons:Ready"],
        JobDefinitionCleanupState.Queued => localizer["Cleanup:Reasons:Queued"],
        JobDefinitionCleanupState.Running => localizer["Cleanup:Reasons:Running"],
        JobDefinitionCleanupState.OwnerUnconfirmed => localizer["Cleanup:Reasons:OwnerUnconfirmed"],
        JobDefinitionCleanupState.Published => localizer["Cleanup:Reasons:Published"],
        _ => localizer["Cleanup:Reasons:Missing"]
    };

    internal static MoVisualRole Role(JobDefinitionCleanupState state) => state switch
    {
        JobDefinitionCleanupState.Ready => MoVisualRole.Success,
        JobDefinitionCleanupState.Queued => MoVisualRole.Warning,
        JobDefinitionCleanupState.Running => MoVisualRole.Info,
        JobDefinitionCleanupState.Published => MoVisualRole.Primary,
        _ => MoVisualRole.Neutral
    };
}
