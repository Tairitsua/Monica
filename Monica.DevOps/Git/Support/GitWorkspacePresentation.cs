using System.Globalization;
using Microsoft.Extensions.Localization;
using Monica.DevOps.Git.Models;
using MudBlazor;

namespace Monica.DevOps.Git.Support;

/// <summary>Formats accepted Git evidence without adding connectivity, authentication, or operation claims.</summary>
/// <remarks>Callers redact service strings before passing them to ordinary Razor text. This formatter owns
/// presentation only and never reads repositories, credentials, options, or the current clock.</remarks>
public static class GitWorkspacePresentation
{
    /// <summary>Formats the actual runtime state with the module's localized vocabulary.</summary>
    public static string StateText(GitRepositorySyncState state, IStringLocalizer localizer) => state switch
    {
        GitRepositorySyncState.NotReady => localizer["GitDashboard:States:NotReady"],
        GitRepositorySyncState.Syncing => localizer["GitDashboard:States:Syncing"],
        GitRepositorySyncState.Ready => localizer["GitDashboard:States:Ready"],
        GitRepositorySyncState.Failed => localizer["GitDashboard:States:Failed"],
        _ => state.ToString()
    };

    /// <summary>Gets semantic ink for actual runtime evidence; Ready is not a connectivity test.</summary>
    public static Color StateColor(GitRepositorySyncState state) => state switch
    {
        GitRepositorySyncState.Ready => Color.Success,
        GitRepositorySyncState.Syncing => Color.Info,
        GitRepositorySyncState.Failed => Color.Error,
        _ => Color.Default
    };

    /// <summary>Formats an independently observed Boolean fact.</summary>
    public static string BoolText(bool value, IStringLocalizer localizer) =>
        value ? localizer["GitDashboard:States:Yes"] : localizer["GitDashboard:States:No"];

    /// <summary>Supplies a localized absence marker for an already-redacted value.</summary>
    public static string Display(string? value, IStringLocalizer localizer) =>
        string.IsNullOrWhiteSpace(value) ? localizer["GitDashboard:States:None"] : value;

    /// <summary>Formats a timestamp with the shared deployment timezone and page display policy.</summary>
    public static string FormatDate(DateTimeOffset? value, IStringLocalizer localizer, Monica.UI.Shared.Services.MoTimeFormatter time) =>
        (value is { } timestamp ? time.Format(timestamp) : null)
        ?? localizer["GitDashboard:States:None"];

    /// <summary>Formats the reported byte count as IEC units and retains the exact unrounded count.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double size = bytes;
        var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return index == 0 ? $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} B"
            : $"{size.ToString("0.##", CultureInfo.CurrentCulture)} {units[index]} ({bytes.ToString("N0", CultureInfo.CurrentCulture)} B)";
    }

    /// <summary>Formats the current stage and its optional stage percentage, never an overall percentage.</summary>
    public static string ProgressText(GitRepositoryStatus repository, IStringLocalizer localizer)
    {
        var stage = ProgressStageText(repository.ProgressStage, localizer);
        return repository.ProgressPercent is { } percent && !string.IsNullOrWhiteSpace(repository.ProgressStage)
            ? $"{stage} ({percent.ToString("0", CultureInfo.CurrentCulture)}%)" : stage;
    }

    /// <summary>Formats an actual known stage, preserving unknown non-secret values for inspection.</summary>
    public static string ProgressStageText(string? stage, IStringLocalizer localizer) => stage switch
    {
        GitRepositoryProgressStages.Preparing => localizer["GitDashboard:ProgressStages:Preparing"],
        GitRepositoryProgressStages.Cloning => localizer["GitDashboard:ProgressStages:Cloning"],
        GitRepositoryProgressStages.Fetching => localizer["GitDashboard:ProgressStages:Fetching"],
        GitRepositoryProgressStages.Checkout => localizer["GitDashboard:ProgressStages:Checkout"],
        GitRepositoryProgressStages.Deleting => localizer["GitDashboard:ProgressStages:Deleting"],
        _ => Display(stage, localizer)
    };

    /// <summary>Formats configured authentication kind without implying successful authentication.</summary>
    public static string CredentialTypeText(GitAuthenticationType type, IStringLocalizer localizer) => type switch
    {
        GitAuthenticationType.Token => localizer["Workspace:AuthenticationTypes:Token"],
        GitAuthenticationType.UserPassword => localizer["Workspace:AuthenticationTypes:UserPassword"],
        _ => type.ToString()
    };
}
