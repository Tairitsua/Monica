using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;
using Monica.BuildingBlocks.Services.Support;
using Monica.Repository.Entity.Abstractions;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// entities and aggregates
/// </summary>
public class EntityBuildingBlock : BuildingBlock
{
    internal EntityBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.Entity, catalog)
    {
    }

    /// <summary>
    /// Gets or sets whether the represented entity is an aggregate root.
    /// </summary>
    public bool IsAggregate { get; set; }

    /// <summary>
    ///  Need to add config.AddDbContext in Program.cs
    /// </summary>
    public RepositoryBuildingBlock? Repository { get; set; }

    protected override BuildingBlockNamingRule? DefaultConventionOption()
    {
        return new BuildingBlockNamingRule
        {
            NamespaceContains = "Entities"
        };
    }

    internal static BuildingBlock Create(BusinessTypeShape shape, BuildingBlockCatalog catalog)
    {
        var unit = new EntityBuildingBlock(shape, catalog);
        unit.CheckMode();
        return unit;
    }
    public override void DeclareRelevance(BuildingBlock unit, bool isDependent = false)
    {
        if (unit is RepositoryBuildingBlock {IsHistoryRepo: false} repo)
        {
            Repository = repo;
        }

        base.DeclareRelevance(unit, isDependent);
    }
}
