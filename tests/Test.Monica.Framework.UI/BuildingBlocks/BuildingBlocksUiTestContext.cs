using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.Core.Results;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.UIBuildingBlocks.Services;
using Monica.BuildingBlocks.Models;
using Monica.Testing.Localization;
using MudBlazor;
using MudBlazor.Services;

namespace Test.Monica.Framework.UI.BuildingBlocks;

internal sealed class BuildingBlocksUiTestContext : BunitContext
{
    internal IRenderedComponent<MudDialogProvider> DialogProvider { get; }

    internal BuildingBlocksUiTestContext(IBuildingBlocksUiDataSource dataSource)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddOptions<global::Monica.Modules.ModuleShellUIOption>();
        Services.AddOptions<global::Monica.Modules.ModuleClockOption>();
        Services.AddSingleton<global::Monica.UI.Shared.Services.MoTimeFormatter>();
        Services.AddSingleton<IStringLocalizer<BuildingBlocksResource>, EchoStringLocalizer<BuildingBlocksResource>>();
        Services.AddSingleton(dataSource);
        _ = Render<MudPopoverProvider>();
        DialogProvider = Render<MudDialogProvider>();
    }
}

internal sealed class StubBuildingBlocksUiDataSource(
    Result<BuildingBlockDashboardSnapshot> dashboardResult,
    Result<BuildingBlockDetail>? detailResult = null)
    : IBuildingBlocksUiDataSource
{
    public Task<Result<BuildingBlockDashboardSnapshot>> GetDashboardAsync()
    {
        return Task.FromResult(dashboardResult);
    }

    public Task<Result<BuildingBlockDetail>> GetDetailAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return detailResult is not null
            ? Task.FromResult(detailResult)
            : Task.FromResult(Result.Failure<BuildingBlockDetail>("No detail configured."));
    }
}
