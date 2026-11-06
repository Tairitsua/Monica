using Monica.Tool.Diagnostics;

namespace Monica.ModelQuery.Models;

/// <summary>
/// A normalized Dynamic LINQ filter with its parameter values and source tokenization context.
/// Use it with a query provider whose source shape matches the normalization semantics.
/// </summary>
/// <param name="finalExpression">The generated Dynamic LINQ expression.</param>
/// <param name="params">The parameter values referenced by the expression.</param>
/// <param name="context">The original expression and resolved tokens.</param>
public class NormalizedFilter(string finalExpression, List<object?> @params, TokenizerContext context)
{
    /// <summary>Gets or sets the generated Dynamic LINQ expression.</summary>
    public string FinalExpression { get; set; } = finalExpression;
    /// <summary>Gets or sets the values referenced by the expression's parameter placeholders.</summary>
    public List<object?> Params { get; set; } = @params;
    /// <summary>Gets the source expression and resolved tokens used to create the filter.</summary>
    public TokenizerContext Context { get; } = context;

    public override string ToString()
    {
        return $"Generated expression:{FinalExpression}\nparams:{Params.Select((p, i) =>
            new
            {
                Index = $"@{i}",
                Type = p?.GetType().Name ?? "null",
                Value = p?.ToJsonString() ?? "null"
            }).ToJsonString()}";
    }
}
