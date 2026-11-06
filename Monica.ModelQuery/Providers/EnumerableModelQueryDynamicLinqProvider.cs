using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.CustomTypeProviders;
using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using Monica.ModelQuery.Abstractions;
using Monica.ModelQuery.Models;
using Monica.ModelQuery.Services;
using Monica.ModelQuery.Utils;
using Monica.Tool.Extensions;
using Monica.Tool.Text;

namespace Monica.ModelQuery.Providers;

public class EnumerableModelQueryDynamicLinqProvider<TModel> : ModelQueryBase<TModel>, IEnumerableModelQuery<TModel>
    where TModel : class
{
    private readonly ParsingConfig _config = CreateParsingConfig();

    private readonly IModelQueryExpressionNormalizer<TModel> _normalizer;

    public EnumerableModelQueryDynamicLinqProvider(IModelQueryExpressionNormalizer<TModel> normalizer, IOptions<ModelQueryExpressionOptions> options, IModelQuerySnapshot<TModel> snapshot) : base(normalizer, options, snapshot)
    {
        normalizer.SetToLinqToObject();
        _normalizer = normalizer;
    }

    public Func<TModel, bool> CompileFilter(NormalizedFilter result)
    {
        var func = (Func<TModel, bool>)DynamicExpressionParser.ParseLambda(_config, typeof(TModel), typeof(bool), result.FinalExpression,
            [.. result.Params]).Compile();
        return func;
    }
    public Func<TModel, bool> CompileFilter(string filter)
    {
        var result = _normalizer.NormalizeFilter(filter);
        var func = (Func<TModel, bool>)DynamicExpressionParser.ParseLambda(_config, typeof(TModel), typeof(bool), result.FinalExpression,
            [.. result.Params]).Compile();
        return func;
    }

    public IEnumerable<TModel> ApplyFilter(IEnumerable<TModel> queryable, string filter)
    {
        var result = _normalizer.NormalizeFilter(filter);
        return ApplyFilter(queryable, result);
    }

    public IEnumerable<TModel> ApplyFilter(IEnumerable<TModel> queryable, NormalizedFilter result)
    {
        return queryable.Where(CompileFilter(result));
    }

    public virtual IEnumerable<TModel> ApplyFilter(IEnumerable<TModel> queryable, Expression<Func<TModel, object>> selector, EFieldConditions condition, string value)
    {
        return ApplyFilter(queryable, $"{GetQueryName(selector)} {condition.GetEnumAlias()} \"{value}\""); // TODO: escape values?
    }
    public IEnumerable<TModel> ApplyFuzzy(IEnumerable<TModel> queryable, string fuzzy, string? fuzzyColumns = null)
    {
        var result = _normalizer.NormalizeFuzzy(fuzzy, fuzzyColumns);
        return ApplyFilter(queryable, result);
    }

    public IEnumerable<dynamic> SelectFields(IEnumerable<TModel> queryable, string selectColumns)
    {
        var selector = _normalizer.NormalizeSelectColumns(selectColumns);
        dynamic func = DynamicExpressionParser.ParseLambda(_config, true, typeof(TModel), null, selector, null).Compile();
        return Enumerable.Select(queryable, func);
    }

    public IEnumerable<dynamic> SelectFieldsExcept(IEnumerable<TModel> queryable, string selectExceptColumns)
    {
        var selector = _normalizer.NormalizeSelectColumns(selectExceptColumns, true);
        dynamic func = DynamicExpressionParser.ParseLambda(_config, true, typeof(TModel), null, selector, null).Compile();
        return Enumerable.Select(queryable, func);
    }

    private static ParsingConfig CreateParsingConfig()
    {
        var config = new ParsingConfig();
        config.CustomTypeProvider = new LinqToObjectCustomProvider(config);
        return config;
    }
}

file class LinqToObjectCustomProvider(ParsingConfig config) : DefaultDynamicLinqCustomTypeProvider(config, [], true)
{
    public override HashSet<Type> GetCustomTypes()
    {
        var result = base.GetCustomTypes();
        result.Add(typeof(LinqToObjectFunctions));
        return result;
    }
}
