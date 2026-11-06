using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Options;
using Monica.ModelQuery.Abstractions;
using Monica.ModelQuery.Exceptions;
using Monica.ModelQuery.Models;
using Monica.Tool.Extensions;

namespace Monica.ModelQuery.Services;

/// <summary>
/// Provides shared field resolution and filter normalization for model query source providers.
/// </summary>
public abstract class ModelQueryBase<TModel>(IModelQueryExpressionNormalizer<TModel> normalizer,
    IOptions<ModelQueryExpressionOptions> options, IModelQuerySnapshot<TModel> snapshot) : IModelQuery<TModel> where TModel : class
{
    /// <summary>
    /// Resolves a property selector to an accepted query name for the same navigation path.
    /// </summary>
    protected string GetQueryName(Expression<Func<TModel, object>> selector)
    {
        Expression? expression = selector.Body;
        var path = new Stack<string>();
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
        {
            expression = conversion.Operand;
        }

        while (expression is MemberExpression { Member: PropertyInfo property } member)
        {
            path.Push(property.Name);
            expression = member.Expression;
        }

        if (expression != selector.Parameters[0] || path.Count == 0)
        {
            throw new ArgumentException("The selector must identify a model property path.", nameof(selector));
        }

        var field = snapshot.GetFields().FirstOrDefault(candidate =>
            (candidate.NavigationProperties?.Select(segment => segment.RefelectName) ?? [])
            .Append(candidate.ReflectionName).SequenceEqual(path, StringComparer.Ordinal));
        return field?.QueryNames.First() ?? throw new ModelQueryNormalizationException(
            displayMessage: "The selected property is not available for model queries.",
            technicalDetail: $"Model: {typeof(TModel).FullName}; property path: {string.Join('.', path)}.");
    }

    public List<QueryField> GetFields(params string[] selectProperties)
    {
        return normalizer.NormalizeLiteralSelect(selectProperties.StringJoin(options.Value.SelectSeparator));
    }

    public List<QueryField> GetFieldsExcept(params string[] selectProperties)
    {
        return normalizer.NormalizeLiteralSelect(selectProperties.StringJoin(options.Value.SelectSeparator), true);
    }

    public List<QueryField> NormalizeLiteralSelect(string selectExpression, bool isReverseSelect = false)
    {
        return normalizer.NormalizeLiteralSelect(selectExpression, isReverseSelect);
    }
    public (List<QueryField> fields, List<string> failedList) NormalizeLiteralSelectWithoutException(string selectExpression, bool isReverseSelect = false)
    {
        return normalizer.NormalizeLiteralSelectWithoutException(selectExpression, isReverseSelect);
    }

    public NormalizedFilter NormalizeFilter(string filter)
    {
        return normalizer.NormalizeFilter(filter);
    }
}
