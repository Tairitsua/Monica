using Microsoft.Extensions.Localization;
using Monica.JobScheduler.UI.Components;
using Monica.JobScheduler.UI.Localization;
using MudBlazor;

namespace Monica.JobScheduler.UI.UIJobScheduler.Executions.Support;

/// <summary>
/// Opens execution evidence consistently without changing the caller's current route.
/// </summary>
internal static class ExecutionDetailDialogLauncher
{
    private static DialogOptions DialogOptions { get; } = new()
    {
        CloseButton = true,
        CloseOnEscapeKey = true,
        FullWidth = true,
        MaxWidth = MaxWidth.False,
        Position = DialogPosition.CenterRight,
        BackgroundClass = "scheduler-execution-backdrop"
    };

    /// <summary>
    /// Opens one execution-detail dialog and returns after the dialog is visible.
    /// </summary>
    internal static Task<IDialogReference> OpenAsync(
        IDialogService dialogService,
        IStringLocalizer<JobSchedulerResource> localizer,
        string instanceId)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var parameters = new DialogParameters<ExecutionDetailDialog>
        {
            { dialog => dialog.InstanceId, instanceId }
        };
        return dialogService.ShowAsync<ExecutionDetailDialog>(
            localizer["ExecutionDetail:Title"],
            parameters,
            DialogOptions);
    }

    /// <summary>
    /// Opens one execution-detail dialog and observes it until it closes.
    /// </summary>
    internal static async Task ShowAsync(
        IDialogService dialogService,
        IStringLocalizer<JobSchedulerResource> localizer,
        string instanceId)
    {
        var dialog = await OpenAsync(dialogService, localizer, instanceId);
        await dialog.Result;
    }
}
