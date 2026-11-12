namespace Monica.EventBus.Models;

/// <summary>Owns ordered batch selection for receive pipeline policies such as Inbox deduplication.</summary>
/// <remarks>
/// The execution input remains the original payload list. Policies retain a subset here before invoking the
/// next delegate; the terminal adapter passes only that subset to the handler. Instances belong to one attempt.
/// </remarks>
public sealed class EventBatchExecutionFeature
{
    private int[] _indices;

    /// <summary>Creates a selection containing every input message in order.</summary>
    public EventBatchExecutionFeature(IReadOnlyList<EventDeliveryMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Metadata = Array.AsReadOnly(metadata.ToArray());
        _indices = Enumerable.Range(0, metadata.Count).ToArray();
    }

    /// <summary>Gets metadata for the currently selected payloads.</summary>
    public IReadOnlyList<EventDeliveryMetadata> Metadata { get; private set; }

    internal IReadOnlyList<int> Indices => _indices;

    /// <summary>Retains strictly increasing indices relative to the current selection, including an empty selection.</summary>
    /// <remarks>Policies may remove entries, but cannot reorder or restore previously removed entries.</remarks>
    public void Retain(IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var selected = indices.ToArray();
        for (var i = 0; i < selected.Length; i++)
            if (selected[i] < 0 || selected[i] >= _indices.Length || (i > 0 && selected[i] <= selected[i - 1]))
                throw new ArgumentException("Retained indices must be in range, distinct and increasing.", nameof(indices));
        Metadata = Array.AsReadOnly(selected.Select(index => Metadata[index]).ToArray());
        _indices = selected.Select(index => _indices[index]).ToArray();
    }
}
