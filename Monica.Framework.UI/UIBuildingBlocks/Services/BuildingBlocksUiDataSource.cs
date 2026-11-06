using Monica.Core.Results;
using Monica.BuildingBlocks.Facades;
using Monica.BuildingBlocks.Models;

namespace Monica.Framework.UI.UIBuildingBlocks.Services;

internal interface IBuildingBlocksUiDataSource
{
    Task<Result<BuildingBlockDashboardSnapshot>> GetDashboardAsync();

    Task<Result<BuildingBlockDetail>> GetDetailAsync(
        string key,
        CancellationToken cancellationToken = default);
}

internal sealed class BuildingBlocksUiDataSource(BuildingBlocksFacade facade) : IBuildingBlocksUiDataSource
{
    public Task<Result<BuildingBlockDashboardSnapshot>> GetDashboardAsync()
    {
        return facade.GetDashboardAsync();
    }

    public Task<Result<BuildingBlockDetail>> GetDetailAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        return facade.GetDetailAsync(key, cancellationToken);
    }
}
