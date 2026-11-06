using Monica.ModelQuery.Models;

namespace Monica.ModelQuery.Abstractions;

public interface IModelQueryExpressionTokenizer<TModel>
{
    /// <summary>
    /// Tokenizes the supplied filter expression.
    /// </summary>
    /// <param name="expression">The raw filter expression.</param>
    /// <returns>The tokenization context produced from the expression.</returns>
    TokenizerContext Tokenize(string expression)
    {
        var context = new TokenizerContext(expression);
        ExtractComponent(context);
        return context;
    }

    void ExtractComponent(TokenizerContext context);
    bool NormalizeField(FieldToken token);
    bool NormalizeCondition(FieldToken token);
    void NormalizeValue(FieldToken token);

    NormalizedFilter GenFinalExpression(TokenizerContext context);
}
