using Monica.EventBus.Models;

namespace Monica.EventBus.Abstractions;

/// <summary>Prepares and stages required events within the current writable operation.</summary>
public interface ITransactionalEventSink
{
    /// <summary>
    /// Validates operation ownership, invokes synchronous preparation once, and stages the complete
    /// captured batch without committing. Exceptions propagate; a caller that handles them must
    /// explicitly abort if partially staged work must not commit.
    /// </summary>
    ValueTask StageAsync(Func<IReadOnlyList<EventMessage>> prepare, CancellationToken cancellationToken);
}
