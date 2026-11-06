using Monica.ModelQuery.Abstractions;

namespace Monica.ModelQuery.Models;

/// <summary>
/// A parsed model filter reusable for queryable and in-memory execution through services in its originating host.
/// Parse it again when using a different host, whose model metadata may differ.
/// </summary>
/// <typeparam name="TModel">The model type whose host-owned metadata was used for parsing.</typeparam>
public sealed class ParsedFilter<TModel> where TModel : class
{
    internal ParsedFilter(IModelQuerySnapshot<TModel> snapshot, string expression, IReadOnlyList<ParsedFilterToken> tokens)
    {
        Snapshot = snapshot;
        Expression = expression;
        Tokens = tokens.ToArray();
        ReferencedFields = Array.AsReadOnly(Tokens.Select(token => token.Field).Distinct().ToArray());
    }
    internal IModelQuerySnapshot<TModel> Snapshot { get; }
    internal string Expression { get; }
    internal ParsedFilterToken[] Tokens { get; }
    /// <summary>The immutable field metadata referenced by this filter, in first-occurrence order.</summary>
    public IReadOnlyList<QueryField> ReferencedFields { get; }
}

internal sealed record ParsedFilterToken(
    string FieldName, string Condition, string Value, int Start, int End, QueryField Field,
    EFieldConditions Conditions, EFieldConditionFeatures Features, object? ConvertedValue);
