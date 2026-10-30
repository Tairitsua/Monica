using Microsoft.EntityFrameworkCore;
using Monica.Repository.Persistence.Models;

namespace Monica.Repository.Persistence.Abstractions;

/// <summary>
/// Extends save-time storage policy after Monica's soft-delete, audit and concurrency rules.
/// Implementations may stamp changed entries, but must not save, publish messages, or change the entry graph.
/// Routing keys needed before EF chooses a physical store must be assigned earlier by the provider adapter.
/// </summary>
public interface IPersistencePolicy
{
    /// <summary>Applies synchronous, deterministic storage rules to the captured batch.</summary>
    /// <remarks>Throw when the policy cannot continue. The original exception escapes and faults the owning operation.</remarks>
    void Apply(DbContext context, IReadOnlyList<PersistenceChange> changes);
}
