using System.Text.Json;
using System.Text.Json.Nodes;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.BuildingBlocks.Abstractions;
using Monica.BuildingBlocks.Models;
using Monica.Tool.Extensions;

namespace Monica.BuildingBlocks.Services;

internal sealed class BuildingBlockCatalogService(
    IDistributedEventBus eventBus,
    IJsonSerializerOptionsProvider jsonSerializerOptionsProvider,
    IBuildingBlockCatalog catalog,
    BuildingBlockProjectionService projections,
    IRequestFilter? requestFilter = null)
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

    internal async Task<object> PublishDomainEventAsync(string eventKey, JsonNode eventContent)
    {
        if ((catalog.FindByFullName<DomainEventBuildingBlock>(eventKey)
             ?? catalog.FindByName<DomainEventBuildingBlock>(eventKey)) is not { } unitEvent)
        {
            throw new KeyNotFoundException(
                $"Building-block metadata for domain event '{eventKey}' was not found or its short name is ambiguous. " +
                "Use the exact full CLR event key from the domain-event catalog.");
        }

        var json = eventContent.ToString();
        var eventToPublish = JsonSerializer.Deserialize(json, unitEvent.ClrType, jsonSerializerOptionsProvider.SerializerOptions)
            ?? throw new InvalidOperationException($"Domain event '{eventKey}' could not be deserialized.");

        await eventBus.PublishAsync(unitEvent.ClrType, eventToPublish);
        return eventToPublish;
    }

    internal BuildingBlockRequestFilterState GetRequestFilterState()
    {
        return new BuildingBlockRequestFilterState
        {
            IsAvailable = requestFilter is not null,
            DisabledPaths = requestFilter is null ? [] : requestFilter.GetDisabledUrls().AsReadOnly()
        };
    }

    internal List<string> ManageRequestFilter(List<string>? urls, bool? disable)
    {
        if (requestFilter == null)
        {
            return [];
        }

        if (urls is { } urlList && disable is { } disableFlag)
        {
            foreach (var url in urlList)
            {
                if (disableFlag)
                {
                    requestFilter.Disable(url);
                }
                else
                {
                    requestFilter.Enable(url);
                }
            }
        }

        return requestFilter.GetDisabledUrls();
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
