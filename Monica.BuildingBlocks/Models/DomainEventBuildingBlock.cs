using Monica.EventBus.Events;
using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;
using Monica.BuildingBlocks.Services.Support;
using Monica.Tool.Extensions;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Describes a discovered domain-event payload.
/// </summary>
public class DomainEventBuildingBlock : BuildingBlock
{
    internal DomainEventBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.DomainEvent, catalog)
    {
    }

    protected override BuildingBlockNamingRule? DefaultConventionOption()
    {
        return new BuildingBlockNamingRule
        {
            Prefix = "Event"
        };
    }

    internal static BuildingBlock Create(BusinessTypeShape shape, BuildingBlockCatalog catalog)
    {
        var unit = new DomainEventBuildingBlock(shape, catalog);
        unit.CheckMode();
        return unit;
    }

    /// <summary>
    /// Creates a representative event payload populated with recursively constructed default reference values.
    /// </summary>
    /// <returns>A representative payload instance, or <see langword="null"/> when the event cannot be instantiated.</returns>
    /// <remarks>
    /// Types without a public parameterless constructor have no generated sample. Nested values with the same
    /// limitation, or types that would repeat the current construction path, are represented by null.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a writable reference-type property cannot be populated with its representative default value.
    /// </exception>
    public object? GetStructure()
    {
        return GetDefaultPocoObject(ClrType, []);
    }

    private static object? GetDefaultPocoObject(Type pocoType, HashSet<Type> constructionPath)
    {
        if (pocoType.IsAbstract || pocoType.GetConstructor(Type.EmptyTypes) is null
            || !constructionPath.Add(pocoType))
        {
            return null;
        }

        try
        {
            var newObj = Activator.CreateInstance(pocoType);
            if (pocoType.IsImplementInterfaceGeneric(typeof(IEnumerable<>))) return newObj;
            var properties = pocoType.GetProperties();
            foreach (var propertyInfo in properties)
            {
                if (propertyInfo.PropertyType.IsClassObject() && propertyInfo.CanWrite)
                {
                    try
                    {
                        propertyInfo.SetValue(newObj, GetDefaultPocoObject(propertyInfo.PropertyType, constructionPath));
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            $"Failed to assign a default value to property '{propertyInfo.Name}' on " +
                            $"{pocoType.GetCleanFullName()}. The property type is " +
                            $"{propertyInfo.PropertyType.GetCleanFullName()}.",
                            exception);
                    }
                }
            }

            return newObj;
        }
        finally
        {
            constructionPath.Remove(pocoType);
        }
    }
}
