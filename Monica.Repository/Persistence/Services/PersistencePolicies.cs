using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Models;
using Monica.Repository.Persistence.Services.Support;

namespace Monica.Repository.Persistence.Services;

internal static class PersistencePolicies
{
    public static IReadOnlyList<PersistenceChange> Apply(
        DbContext context, IAuditPropertySetter audit, IEnumerable<IPersistencePolicy> extensions)
    {
        if (context.ChangeTracker.AutoDetectChangesEnabled) context.ChangeTracker.DetectChanges();
        ResolveDeferredCascades(context.ChangeTracker);
        var entries = context.ChangeTracker.Entries().ToArray();
        PreserveSoftDeletedDependents(entries);
        var changes = entries.Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => new PersistenceChange(entry, entry.State switch
            {
                EntityState.Added => PersistenceChangeKind.Created,
                EntityState.Deleted => PersistenceChangeKind.Deleted,
                _ when entry.Entity is IHasSoftDelete { IsDeleted: true }
                    && !entry.Property(nameof(IHasSoftDelete.IsDeleted)).OriginalValue!.Equals(true) => PersistenceChangeKind.Deleted,
                _ => PersistenceChangeKind.Updated
            })).ToArray();

        foreach (var change in changes)
        {
            var entry = change.Entry;
            if (change.Kind == PersistenceChangeKind.Created && entry.Metadata.FindPrimaryKey() is { } key)
                foreach (var property in key.Properties)
                    ApplicationKeyValidation.Validate(property, entry.Property(property.Name).CurrentValue, entry.Property(property.Name).IsTemporary);
            if (entry.State == EntityState.Deleted && entry.Entity is IHasSoftDelete)
            {
                // Deletion wins over unflushed scalar edits; retained owned data is restored below.
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
                Set(entry, nameof(IHasSoftDelete.IsDeleted), true);
            }

            var before = entry.CurrentValues.Clone();
            if (change.Kind == PersistenceChangeKind.Created)
                audit.SetCreationProperties(entry.Entity);
            else if (entry.State != EntityState.Deleted)
            {
                audit.IncrementEntityVersionProperty(entry.Entity);
                audit.SetModificationProperties(entry.Entity);
                if (change.Kind == PersistenceChangeKind.Deleted) audit.SetDeletionProperties(entry.Entity);
            }

            if (entry.Entity is IHasConcurrencyStamp stamp && entry.State != EntityState.Deleted)
            {
                if (change.Kind != PersistenceChangeKind.Created || string.IsNullOrEmpty(stamp.ConcurrencyStamp))
                    Set(entry, nameof(IHasConcurrencyStamp.ConcurrencyStamp), Guid.NewGuid().ToString("N"));
                // OriginalValue remains EF's load-time token, including across repeated successful flushes.
            }
            MarkPolicyChanges(entry, before);
        }

        foreach (var extension in extensions)
        {
            var before = changes.Select(x => x.Entry.CurrentValues.Clone()).ToArray();
            extension.Apply(context, changes);
            for (var i = 0; i < changes.Length; i++) MarkPolicyChanges(changes[i].Entry, before[i]);
        }
        return changes;
    }

    private static void ResolveDeferredCascades(ChangeTracker tracker)
    {
        if (tracker.CascadeDeleteTiming != CascadeTiming.OnSaveChanges
            && tracker.DeleteOrphansTiming != CascadeTiming.OnSaveChanges) return;

        // CascadeChanges forces both kinds of cascade, including those explicitly disabled by Never.
        // Resolve deferred changes before capturing policies without silently overriding that protection.
        if (tracker.CascadeDeleteTiming == CascadeTiming.Never
            || tracker.DeleteOrphansTiming == CascadeTiming.Never)
            throw new NotSupportedException(
                "Save policies cannot combine OnSaveChanges and Never cascade timing. Use Immediate for the enabled cascade behavior.");

        tracker.CascadeChanges();
    }

    private static void MarkPolicyChanges(EntityEntry entry, PropertyValues before)
    {
        if (entry.State is EntityState.Added or EntityState.Deleted) return;
        foreach (var property in entry.Properties)
            if (!Equals(before[property.Metadata.Name], property.CurrentValue))
                property.IsModified = true;
    }

    private static void Set(EntityEntry entry, string property, object value)
    {
        entry.Property(property).CurrentValue = value;
        if (entry.State != EntityState.Added) entry.Property(property).IsModified = true;
    }

    private static void PreserveSoftDeletedDependents(EntityEntry[] entries)
    {
        var retained = entries.Where(x => x.State == EntityState.Deleted && x.Entity is IHasSoftDelete).ToHashSet();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var dependent in entries.Where(x => x.State != EntityState.Detached && !retained.Contains(x)))
            {
                foreach (var foreignKey in dependent.Metadata.GetForeignKeys())
                {
                    var principal = retained.FirstOrDefault(x => foreignKey.PrincipalEntityType.IsAssignableFrom(x.Metadata)
                        && foreignKey.Properties.Select(p => dependent.Property(p.Name).OriginalValue)
                            .SequenceEqual(foreignKey.PrincipalKey.Properties.Select(p => x.Property(p.Name).OriginalValue)));
                    if (principal is null) continue;
                    if (!foreignKey.IsOwnership)
                    {
                        if (dependent.State == EntityState.Deleted)
                            throw new InvalidOperationException("Soft deletion cannot cascade a physical delete to another aggregate. Configure Restrict and delete explicitly.");
                        continue;
                    }
                    retained.Add(dependent);
                    if (dependent.State == EntityState.Added)
                        dependent.State = EntityState.Detached;
                    else
                    {
                        dependent.CurrentValues.SetValues(dependent.OriginalValues);
                        dependent.State = EntityState.Unchanged;
                    }
                    changed = true;
                    break;
                }
            }
        }
    }
}
