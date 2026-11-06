namespace Monica.ModelQuery.Models;

/// <summary>
/// Configures the separators and tokens used when normalizing model query expressions.
/// Configure these options before model query services are created.
/// </summary>
public class ModelQueryExpressionOptions
{
    /// <summary>Separates values during multi-value conversion. Defaults to a comma.</summary>
    public string FilterMultiSeparator { get; set; } = ",";
    /// <summary>Identifies an existing wildcard in an <c>explike</c> value. Defaults to <c>%</c>.</summary>
    public string Fuzzy { get; set; } = "%";
    /// <summary>
    /// Logical AND token emitted for <c>explike</c> expressions. Defaults to <c>and</c>.
    /// </summary>
    public string And { get; set; } = "and";
    /// <summary>
    /// Logical OR token emitted for <c>explike</c> expressions. Defaults to <c>or</c>.
    /// </summary>
    public string Or { get; set; } = "or";

    /// <summary>AND token within an <c>explike</c> value. Defaults to <c>&amp;</c>.</summary>
    public string ExpLikeAnd { get; set; } = "&";
    /// <summary>OR token within an <c>explike</c> value. Defaults to <c>|</c>.</summary>
    public string ExpLikeOr { get; set; } = "|";
    /// <summary>Negation token within an <c>explike</c> value. Defaults to <c>!</c>.</summary>
    public string ExpLikeNot { get; set; } = "!";
}
