using Monica.Core.TypeDiscovery.Models;

namespace Monica.BuildingBlocks.Services.Support;

/// <summary>
/// Transfers compiler-owned building-block shapes from Monica type discovery to the host's catalog factory.
/// </summary>
internal sealed class BuildingBlockDiscoveryPlan
{
    private BusinessTypeShape[]? _shapes;

    /// <summary>
    /// Publishes the single immutable discovery result for this host.
    /// </summary>
    internal void Publish(IEnumerable<BusinessTypeShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);

        var snapshot = shapes.ToArray();
        if (Interlocked.CompareExchange(ref _shapes, snapshot, null) is not null)
        {
            throw new InvalidOperationException("Building-block discovery was published more than once for the same host.");
        }
    }

    /// <summary>
    /// Gets the completed discovery result when the DI singleton factory creates the runtime catalog.
    /// </summary>
    internal IReadOnlyList<BusinessTypeShape> GetRequiredSnapshot()
    {
        return Volatile.Read(ref _shapes)
               ?? throw new InvalidOperationException(
                   "Building-block discovery has not completed before the catalog was requested.");
    }
}
