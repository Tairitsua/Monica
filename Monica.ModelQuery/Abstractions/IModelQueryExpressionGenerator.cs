using Monica.ModelQuery.Models;

namespace Monica.ModelQuery.Abstractions;

public interface IModelQueryExpressionGenerator
{
    /// <summary>
    /// Generates the expression for a single field condition.
    /// </summary>
    /// <param name="token">The field token.</param>
    /// <param name="num">The index of the field within the expression.</param>
    /// <param name="totalParamCount">The total number of existing parameters.</param>
    /// <param name="supplementParamObjects">Receives additional parameter objects produced during generation.</param>
    /// <remarks>https://dynamic-linq.net/expression-language#calling-method-and-constructor</remarks>
    /// <returns>The generated token expression.</returns>
    string GenerateTokenExpression(FieldToken token, int num, int totalParamCount,
        out List<object> supplementParamObjects);

}
