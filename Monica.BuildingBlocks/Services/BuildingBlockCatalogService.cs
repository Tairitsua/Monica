using Monica.BuildingBlocks.Abstractions;
using Monica.BuildingBlocks.Models;
using Monica.Tool.Extensions;

namespace Monica.BuildingBlocks.Services;

internal sealed class BuildingBlockCatalogService(
    IBuildingBlockCatalog catalog,
    BuildingBlockProjectionService projections)
{
    internal List<BuildingBlockSummary> GetAll()
    {
        return projections.GetAll();
    }

    internal BuildingBlockDashboardSnapshot GetDashboard()
    {
        return projections.GetDashboard();
    }

    internal Task<BuildingBlockDetail> GetDetailAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        return projections.GetDetailAsync(key, cancellationToken);
    }

    internal List<BuildingBlockDomainEventInfo> GetDomainEvents()
    {
        return projections.GetDomainEvents();
    }

    internal List<DtoAssemblyEnumInfo> GetEnumInfo(string? name = null)
    {
        if (name == null)
        {
            return catalog.EnumTypes
                .GroupBy(item => item.Value.Assembly.FullName)
                .Select(group => new DtoAssemblyEnumInfo
                {
                    From = group.Key,
                    Enums = group.Select(item => new DtoEnumInfo
                    {
                        Name = item.Key,
                        Values = item.Value.GetEnumValues().OfType<Enum>().Select((value, index) => new DtoEnumValue
                        {
                            Index = index,
                            Name = value.ToString(),
                            Description = value.GetDescription(),
                        }).ToList()
                    }).ToList()
                })
                .ToList();
        }

        if (!catalog.EnumTypes.TryGetValue(name, out var enumType))
        {
            throw new KeyNotFoundException($"Enum type '{name}' was not found in the current host's building-block catalog.");
        }

        return
        [
            new DtoAssemblyEnumInfo
            {
                From = enumType.Assembly.FullName,
                Enums =
                [
                    new DtoEnumInfo
                    {
                        Name = name,
                        Values = enumType.GetEnumValues().OfType<Enum>().Select((value, index) => new DtoEnumValue
                        {
                            Index = index,
                            Name = value.ToString(),
                            Description = value.GetDescription(),
                        }).ToList()
                    }
                ]
            }
        ];
    }
}
