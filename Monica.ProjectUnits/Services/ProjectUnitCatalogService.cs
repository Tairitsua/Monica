using System.Text.Json;
using System.Text.Json.Nodes;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.ProjectUnits.Abstractions;
using Monica.ProjectUnits.Models;
using Monica.Tool.Extensions;

namespace Monica.ProjectUnits.Services;

internal sealed class ProjectUnitCatalogService(
    IDistributedEventBus eventBus,
    IJsonSerializerOptionsProvider jsonSerializerOptionsProvider,
    IProjectUnitCatalog catalog,
    ProjectUnitProjectionService projections,
    IRequestFilter? requestFilter = null)
{
    internal List<ProjectUnitSummary> GetAllProjectUnits()
    {
        return projections.GetAllProjectUnits();
    }

    internal ProjectUnitDashboardSnapshot GetDashboard()
    {
        return projections.GetDashboard();
    }

    internal Task<ProjectUnitDetail> GetProjectUnitDetailAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        return projections.GetProjectUnitDetailAsync(key, cancellationToken);
    }

    internal List<ProjectUnitDomainEventInfo> GetDomainEvents()
    {
        return projections.GetDomainEvents();
    }

    internal async Task<object> PublishDomainEventAsync(string eventKey, JsonNode eventContent)
    {
        if ((catalog.FindByFullName<UnitDomainEvent>(eventKey)
             ?? catalog.FindByName<UnitDomainEvent>(eventKey)) is not { } unitEvent)
        {
            throw new KeyNotFoundException(
                $"Project-unit metadata for domain event '{eventKey}' was not found or its short name is ambiguous. " +
                "Use the exact full CLR event key from the domain-event catalog.");
        }

        var json = eventContent.ToString();
        var eventToPublish = JsonSerializer.Deserialize(json, unitEvent.Type, jsonSerializerOptionsProvider.SerializerOptions)
            ?? throw new InvalidOperationException($"Domain event '{eventKey}' could not be deserialized.");

        await eventBus.PublishAsync(unitEvent.Type, eventToPublish);
        return eventToPublish;
    }

    internal ProjectUnitRequestFilterState GetRequestFilterState()
    {
        return new ProjectUnitRequestFilterState
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
            throw new KeyNotFoundException($"Enum type '{name}' was not found in the current host's project-unit catalog.");
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
