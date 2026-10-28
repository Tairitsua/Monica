using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace Monica.EntityFrameworkCore.Internal;

internal sealed class TableLayoutConvention(IDbContextOptions options) : IConventionSetPlugin, IModelFinalizingConvention
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.ModelFinalizingConventions.Add(this);
        return conventionSet;
    }

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        var layout = options.FindExtension<TableLayoutOptionsExtension>()!;
        var model = (IMutableModel)modelBuilder.Metadata;
        var entities = model.GetEntityTypes().ToArray();
        var marked = entities.Where(e => e.FindAnnotation(TableLayoutIdentifiers.TABLE_ANNOTATION) is not null).ToArray();
        var groups = marked.GroupBy(e => (e.GetTableName(), e.GetSchema())).ToArray();
        foreach (var group in groups)
        {
            var table = group.Key;
            foreach (var entity in group)
            {
                if (entity.FindAnnotation(TableLayoutIdentifiers.TABLE_ANNOTATION)!.Value as string != entity.GetTableName())
                    throw new InvalidOperationException($"Infrastructure table '{entity.Name}' was renamed after its declaration.");
                if (entity.FindAnnotation(RelationalAnnotationNames.Schema)?.Value is string explicitSchema && explicitSchema != layout.Schema)
                    throw new InvalidOperationException($"Infrastructure table '{entity.Name}' explicitly selects schema '{explicitSchema}', conflicting with the common layout.");
                if (entity.GetMappingFragments(StoreObjectType.Table).Any())
                    throw new NotSupportedException($"Infrastructure entity splitting is not supported for '{entity.Name}'. Map separate infrastructure entities/tables explicitly.");
            }
            TableLayoutIdentifiers.ValidateLength(table.Item1, layout.Capabilities);
            foreach (var entity in entities.Where(e => (e.GetTableName(), e.GetSchema()) == table).ToArray())
            {
                if (!group.Contains(entity) && !group.Any(owner => TableOwner(entity) == TableOwner(owner)))
                    throw new InvalidOperationException($"Business entity '{entity.Name}' collides with infrastructure table '{table.Item1}'. Use distinct physical table names.");
                entity.SetSchema(layout.Schema);
            }
        }
        model.SetAnnotation(TableLayoutIdentifiers.SCHEMA_ANNOTATION, layout.Schema ?? TableLayoutIdentifiers.DEFAULT_SCHEMA);
        model.SetAnnotation(TableLayoutIdentifiers.PROVIDER_ANNOTATION, layout.ProviderType.FullName!);
    }

    private static IMutableEntityType TableOwner(IMutableEntityType entity)
    {
        while (entity.FindOwnership() is { } ownership) entity = ownership.PrincipalEntityType;
        return entity.GetRootType();
    }
}
