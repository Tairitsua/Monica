using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.CustomTypeProviders;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monica.ModelQuery.Abstractions;
using Monica.ModelQuery.Exceptions;
using Monica.ModelQuery.Models;
using Monica.ModelQuery.Services;
using Monica.Core.Extensions;
using Monica.Tool.Diagnostics;
using Monica.Tool.Extensions;
using Monica.Tool.Text;

namespace Monica.ModelQuery.Providers;

public class QueryableModelQueryDynamicLinqProvider<TModel>(IModelQueryExpressionNormalizer<TModel> normalizer, IOptions<ModelQueryExpressionOptions> options, IModelQuerySnapshot<TModel> snapshot) : ModelQueryBase<TModel>(normalizer, options, snapshot), IQueryableModelQuery<TModel>
    where TModel : class
{
    private readonly ParsingConfig _config = CreateParsingConfig();

    private readonly IModelQueryExpressionNormalizer<TModel> _normalizer = normalizer;

    public IQueryable<TModel> ApplyFilter(IQueryable<TModel> queryable, NormalizedFilter result)
    {
        try
        {
            var query = queryable.Where(_config, result.FinalExpression, [.. result.Params]);
            return query;
        }
        catch (Exception e)
        {
            throw new ModelQueryExecutionException(
                displayMessage: "数据查询执行失败，请检查表达式是否存在语法错误",
                technicalDetail: $"Expr: {result.FinalExpression}, 参数: {result.Params.ToJsonString()}, 错误: {e.GetMessageRecursively()}");
        }
    }

    public virtual IQueryable<TModel> ApplyFilter(IQueryable<TModel> queryable, Expression<Func<TModel, object>> selector, EFieldConditions condition, string value)
    {
        return ApplyFilter(queryable, $"{GetQueryName(selector)} {condition.GetEnumAlias()} \"{value}\""); // TODO: escape values?
    }
    public virtual IQueryable<TModel> ApplyFilter(IQueryable<TModel> queryable, string filter)
    {
        // Debug escape hatch for raw Dynamic LINQ expressions.
        if (filter.StartsWith('[') && filter.EndsWith(']'))
        {
            return queryable.Where(_config, filter.TrimStart('[').TrimEnd(']'));
        }
        var result = _normalizer.NormalizeFilter(filter);
        return ApplyFilter(queryable, result);
    }

    public virtual IQueryable<TModel> ApplyFuzzy(IQueryable<TModel> queryable, string fuzzy, string? fuzzyColumns = null)
    {
        var result = _normalizer.NormalizeFuzzy(fuzzy, fuzzyColumns);
        return ApplyFilter(queryable, result);
    }

    public virtual IQueryable SelectFields(IQueryable<TModel> queryable, string selectColumns)
    {
        return queryable.Select(_normalizer.NormalizeSelectColumns(selectColumns));
    }

    public IQueryable SelectFieldsExcept(IQueryable<TModel> queryable, string selectExceptColumns)
    {
        return queryable.Select(_normalizer.NormalizeSelectColumns(selectExceptColumns, true));
    }

    private static ParsingConfig CreateParsingConfig()
    {
        var config = new ParsingConfig
        {
            AllowEqualsAndToStringMethodsOnObject = true // Required after the v1.6.0 security fix.
        };
        config.CustomTypeProvider = new LinqToSqlCustomProvider(config);
        return config;
    }
}


file class LinqToSqlCustomProvider(ParsingConfig config) : DefaultDynamicLinqCustomTypeProvider(config, [], true)
{
    public override HashSet<Type> GetCustomTypes()
    {
        var result = base.GetCustomTypes();
        result.Add(typeof(EF));
        //result.Add(typeof(NpgsqlFullTextSearchDbFunctionsExtensions));
        //result.Add(typeof(NpgsqlDbFunctionsExtensions));
        result.Add(typeof(DbFunctionsExtensions));
        //result.Add(typeof(DbFunctions));
        return result;
    }
}
