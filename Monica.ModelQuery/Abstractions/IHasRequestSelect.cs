namespace Monica.ModelQuery.Abstractions;

public interface IHasRequestSelect
{
    /// <summary>
    /// Response field list that includes only the specified fields.
    /// </summary>
    string? SelectColumns { get; set; }

    /// <summary>
    /// Response field list that excludes the specified fields. Cannot be used with <see cref="SelectColumns"/>.
    /// </summary>
    string? SelectExceptColumns { get; set; }

    /// <summary>
    /// Determines whether any field-selection option has been provided.
    /// </summary>

    bool HasUsingSelected()
    {
        return !string.IsNullOrWhiteSpace(SelectColumns) || !string.IsNullOrWhiteSpace(SelectExceptColumns);
    }
}
