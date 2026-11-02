using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.AutoModel.Abstractions;
using Monica.AutoModel.Exceptions;
using Monica.AutoModel.Models;
using Monica.AutoModel.Services;
using Monica.Tool.Diagnostics;
using Monica.Tool.Extensions;

namespace Monica.AutoModel.Providers;

public class ExpressionNormalizerDynamicLinqProvider<TModel>(
    IAutoModelSnapshot<TModel> snapshot,
    IOptions<AutoModelExpressionOptions> expressionOptions,
    IMonicaApplicationOptions applicationOptions,
    IAutoModelExpressionTokenizer<TModel> tokenizer,
    ILogger<ExpressionTokenizer<TModel>> logger)
    : IAutoModelExpressionNormalizer<TModel>
{
    protected AutoModelExpressionOptions ExpressionOptions = expressionOptions.Value;
    protected bool LinqToObject = false;

    public List<AutoField> NormalizeLiteralSelect(string columns, bool isReverseSelect = false)
    {
        var (fields, failedList) = NormalizeLiteralSelectWithoutException(columns, isReverseSelect);

        if (failedList.Count <= 0) return fields;
        throw new AutoModelNormalizeException(
            displayMessage: "查询字段无法识别",
            technicalDetail: $"无法识别的字段: {failedList.StringJoin(",")}; 支持的字段: {string.Join(',', snapshot.GetAllActivateNames())}");
    }

    public (List<AutoField> fields, List<string> failedList) NormalizeLiteralSelectWithoutException(string selectExpression,
        bool isReverseSelect = false)
    {
        var fields = new List<AutoField>();
        var errors = new List<string>();
        var all = isReverseSelect ? snapshot.GetFields().Select(GetSelectExpression).ToHashSet() : [];
        foreach (var column in selectExpression.Split(ExpressionOptions.SelectSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (snapshot.GetField(column) is not { } field)
            {
                errors.Add(column);
                continue;
            }

            if (isReverseSelect)
            {
                if (GetSelectExpression(field) is { } name && all.Contains(name))
                {
                    fields.Add(field);
                }
            }
            else
            {
                fields.Add(field);
            }
        }
        return (fields, errors);
    }

    /// <summary>
    /// Builds the Dynamic LINQ select expression for a field.
    /// </summary>
    /// <param name="field">The field metadata to translate.</param>
    /// <returns>The Dynamic LINQ member-access expression for the field.</returns>
    private string GetSelectExpression(AutoField field)
    {
        if (field.NavigationProperties is { } list)
        {
            if (field.NavigationProperties.Any(p => p.IsCollection)) throw new AutoModelNormalizeException(
                displayMessage: "不支持查询集合字段",
                technicalDetail: $"字段 '{field.ReflectionName}' 是集合类型，暂不支持直接查询");

            return list.Select(p => p.RefelectName).CombineForeach([field.ReflectionName]).StringJoin(".");
        }
        return field.ReflectionName;
    }

    public string NormalizeSelectColumns(string selectColumns, bool isReverseSelect = false)
    {
        if (string.IsNullOrWhiteSpace(selectColumns))
            throw new AutoModelNormalizeException(
                displayMessage: "查询字段不能为空",
                technicalDetail: "Select 参数为 null 或空");
        var selectColumnExp = "";
        if (isReverseSelect)
        {
            var excepted = NormalizeLiteralSelect(selectColumns).Select(GetSelectExpression).ToHashSet();
            var all = snapshot.GetFields().Select(GetSelectExpression).ToHashSet();
            selectColumnExp = all.Except(excepted).StringJoin(",");
        }
        else
        {
            selectColumnExp = NormalizeLiteralSelect(selectColumns).Select(GetSelectExpression).StringJoin(",");
        }
        var expression = $"new {{ {selectColumnExp} }}";
        if (applicationOptions.DebugMode)
        {
            logger.LogInformation("Select string: {filter}", expression);
        }

        return expression;
    }

    public NormalizedResult NormalizeFilter(string filter)
    {
        var context = tokenizer.Tokenize(filter);
        foreach (var token in context.Tokens)
        {
            if (LinqToObject)
            {
                token.Features |= EFieldConditionFeatures.UseClientSideEvaluations;
            }
        }
        var result = tokenizer.GenFinalExpression(context);
        if (applicationOptions.DebugMode)
        {
            logger.LogInformation("Filter string: {filter}", context.OriginExpression);
            logger.LogInformation(result.ToString());
        }

        return result;
    }

    public NormalizedResult NormalizeFuzzy(string fuzzy, string? fuzzyColumns = null)
    {
        // TODO: check for quotes, parentheses, or other injection patterns.
        List<AutoField> fuzzyColumnsList;
        if (!string.IsNullOrWhiteSpace(fuzzyColumns))
        {
            fuzzyColumnsList = NormalizeLiteralSelect(fuzzyColumns);
            var unsupported = fuzzyColumnsList.Where(p => p.FuzzSetting.IsNotSupported)
                .Select(p => $"({p.TypeSetting.OriginType.Name}){p.ReflectionName}").StringJoin(",");
            if (!string.IsNullOrWhiteSpace(unsupported))
            {
                throw new AutoModelNormalizeException(
                    displayMessage: "模糊查询字段类型不支持",
                    technicalDetail: $"不支持的字段: {unsupported}");
            }
        }
        else
        {
            fuzzyColumnsList = snapshot.GetFields().Where(x => x.FuzzSetting is { IsIgnored: false, IsNotSupported: false }).ToList();
        }

        //TODO improve performance
        var context = tokenizer.Tokenize(fuzzyColumnsList.Select(p => $"({p.DefaultActiveName} like \"{fuzzy}\")").StringJoin(" or "));
        var result = tokenizer.GenFinalExpression(context);
        if (applicationOptions.DebugMode)
        {
            logger.LogInformation("Filter string: {filter}", context.OriginExpression);
            logger.LogInformation("Generated expression: {expression}", result.FinalExpression);
            logger.LogInformation("value: \n{value}", result.Params.ToJsonString());
        }

        return result;
    }

    public void SetToLinqToObject()
    {
        LinqToObject = true;
    }
}
