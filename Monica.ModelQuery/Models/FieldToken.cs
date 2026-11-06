using Monica.Tool.Annotations;
using Monica.Tool.Extensions;

namespace Monica.ModelQuery.Models;

public class FieldToken(string fieldStr, string conditionStr, string valueStr, int start, int end)
{
    /// <summary>
    /// Field query name in the original expression.
    /// </summary>
    public string FieldStr { get; set; } = fieldStr;

    /// <summary>
    /// Condition token in the original expression.
    /// </summary>
    public string ConditionStr { get; set; } = conditionStr;

    /// <summary>
    /// Value token in the original expression.
    /// </summary>
    public string ValueStr { get; set; } = valueStr;

    /// <summary>
    /// Resolved field metadata.
    /// </summary>
    public QueryField? FieldInfo { get; set; }

    /// <summary>
    /// Resolved field condition.
    /// </summary>
    public EFieldConditions Conditions { get; set; }

    /// <summary>
    /// Resolved field-condition features.
    /// </summary>
    public EFieldConditionFeatures Features { get; set; }

    /// <summary>
    /// Converted value.
    /// </summary>
    public object? ConvertedValue { get; set; }

    /// <summary>
    /// Start index in the original expression.
    /// </summary>
    public int Start { get; set; } = start;

    /// <summary>
    /// End index in the original expression.
    /// </summary>
    public int End { get; set; } = end;

    /// <summary>
    /// Generated token expression.
    /// </summary>
    public string? TokenExpression { get; set; }
    /// <summary>
    /// Gets the field path used in the generated condition expression.
    /// </summary>
    /// <returns>The field parameter expression, or <c>null</c> if the field is unresolved.</returns>
    public string? GetFieldParam()
    {
        if (FieldInfo == null) return null;
        return FieldInfo.GetConditionExpressionParam();
    }

    public override string ToString()
    {
        return
            $"{FieldStr} {ConditionStr} \"{ValueStr}\" -> {FieldInfo} {Conditions} \"{ConvertedValue}\" {Features.GetFlagsString(ignoreEnums: EFieldConditionFeatures.None).BeIfNotEmpty(" with {0}", true)}{TokenExpression.BeIfNotEmpty(" => {0}", true)}";
    }
}

public class TokenizerContext(string originExpression)
{
    public string OriginExpression { get; set; } = originExpression;

    public List<FieldToken> Tokens { get; set; } = [];

    public string GetFinalExpression()
    {
        // TODO: Optimize expressions composed from tokens at the same logical level.
        var final = OriginExpression;
        for (var i = Tokens.Count - 1; i >= 0; i--)
        {
            var token = Tokens[i];
            if (token.TokenExpression is not { } tokenExp) continue;
            var start = token.Start;
            var end = token.End;
            final = final.Remove(start, end - start + 1).Insert(start, tokenExp);
        }

        return final;
    }

}

/// <summary>
/// Field condition features.
/// </summary>
[Flags]
public enum EFieldConditionFeatures
{
    None,
    /// <summary>
    /// Regular-expression matching. Not implemented yet.
    /// </summary>
    UseRegex = 1 << 0,
    /// <summary>
    /// Requires client-side evaluation because the field cannot be handled directly by the database. Not implemented yet.
    /// </summary>
    UseClientSideEvaluations = 1 << 1,
    /// <summary>
    /// Multi-value mode. Enabled by default when both <c>in</c> and <c>,</c> are used.
    /// </summary>
    Multi = 1 << 2,
    /// <summary>
    /// Fuzzy-match mode. Enabled by default when <c>like</c> is used and the field type supports it.
    /// </summary>
    Fuzzy = 1 << 3,

    /// <summary>
    /// Negates the condition result.
    /// </summary>
    Not = 1 << 4,
}

/// <summary>
/// Supported ModelQuery field conditions.
/// </summary>
public enum EFieldConditions
{
    /// <summary>
    /// No condition.
    /// </summary>
    None,
    /// <summary>
    /// Equal to.
    /// </summary>
    [EnumAlias("=")]
    Equal,
    /// <summary>
    /// String pattern match, such as <c>xx%</c>.
    /// </summary>
    [EnumAlias("like")]
    Like,
    /// <summary>
    /// Included in a value list.
    /// </summary>
    [EnumAlias("in")]
    In,
    /// <summary>
    /// Greater than.
    /// </summary>
    [EnumAlias(">")]
    GreaterThan,
    /// <summary>
    /// Less than.
    /// </summary>
    [EnumAlias("<")]
    LessThan,
    /// <summary>
    /// Greater than or equal to.
    /// </summary>
    [EnumAlias(">=")]
    GreaterThanOrEqual,
    /// <summary>
    /// Less than or equal to.
    /// </summary>
    [EnumAlias("<=")]
    LessThanOrEqual,
    /// <summary>
    /// Not equal to.
    /// </summary>
    [EnumAlias("!=")]
    Unequal,
    /// <summary>
    /// Expression-based pattern match.
    /// </summary>
    [EnumAlias("explike")]
    ExpLike,
    /// <summary>
    /// Negated pattern match.
    /// </summary>
    [EnumAlias("notlike")]
    NotLike,
    /// <summary>
    /// Identity check, primarily used for null and non-null checks.
    /// </summary>
    [EnumAlias("is")]
    Is,
}
