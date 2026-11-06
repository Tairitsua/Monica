using Monica.BuildingBlocks.Abstractions;
using Monica.BuildingBlocks.Models;

namespace Monica.BuildingBlocks.Services.Support;

internal sealed class NullBuildingBlockRequirementLinkResolver : IBuildingBlockRequirementLinkResolver
{
    public ValueTask<BuildingBlockRequirementLink?> ResolveAsync(
        string requirementId,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<BuildingBlockRequirementLink?>(null);
    }
}
