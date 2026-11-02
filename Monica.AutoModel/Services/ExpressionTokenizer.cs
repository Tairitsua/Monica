using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.AutoModel.Abstractions;
using Monica.AutoModel.Exceptions;
using Monica.AutoModel.Models;
using Monica.Tool.Extensions;
using Monica.Tool.Text;

namespace Monica.AutoModel.Services;

public partial class ExpressionTokenizer<TModel>(
    IAutoModelSnapshot<TModel> snapshot,
    IAutoModelTypeConverter converter,
    IAutoModelTokenExpressionGen gen,
    IMonicaApplicationOptions applicationOptions,
    ILogger<ExpressionTokenizer<TModel>> logger) : IAutoModelExpressionTokenizer<TModel>
{
    public void ExtractComponent(TokenizerContext context)
    {
        var fieldError = new StringBuilder();
        var conditionError = new StringBuilder();
        var valueError = new StringBuilder();
        var regex = ExpressionRegex();
        foreach (var match in regex.Matches(context.OriginExpression).Cast<Match>())
        {
            var fieldGroup = match.Groups["Field"];
            var conditionGroup = match.Groups["Condition"];
            var valueGroup = match.Groups["Value"];
            var token = new FieldToken(fieldGroup.Value, conditionGroup.Value, valueGroup.Value,
                fieldGroup.Index, valueGroup.Index + valueGroup.Length);

            if (!NormalizeField(token))
            {
                fieldError.Append($"{token.FieldStr},");
                continue;
            }

            if (!NormalizeCondition(token))
            {
                conditionError.Append($"{token.ConditionStr},");
                continue;
            }

            try
            {
                NormalizeValue(token);
            }
            catch (AutoModelValueConvertException e)
            {
                valueError.AppendLine(e.Message);
                continue;
            }

            context.Tokens.Add(token);
        }

        var finalError = new StringBuilder();
        if (fieldError.Length > 0)
        {
            finalError.AppendLine(
                $"字段{fieldError.ToString().TrimEnd(',')}无法识别。支持的激活名有：{string.Join(',', snapshot.GetAllActivateNames())}");
        }

        if (conditionError.Length > 0)
        {
            finalError.AppendLine($"条件{conditionError.ToString().TrimEnd(',')}无法识别。支持的条件有：{GetEnumRange(typeof(EFieldConditions))}");
        }

        if (valueError.Length > 0)
        {
            finalError.AppendLine(valueError.ToString());
        }


        if (finalError.Length > 0)
        {

            if (applicationOptions.DebugMode)
            {
                logger.LogError("Expression tokenize encountered error: {exp}\n Errors:\n{errors}",
                    context.OriginExpression, finalError.ToString().TrimEnd());
            }
            throw new AutoModelNormalizeException(
                displayMessage: "查询表达式解析失败",
                technicalDetail: finalError.ToString().TrimEnd());
        }
    }

    public bool NormalizeField(FieldToken token)
    {
        token.FieldInfo = snapshot.GetField(token.FieldStr);
        return token.FieldInfo != null;
    }

    public bool NormalizeCondition(FieldToken token)
    {
        if (!token.ConditionStr.TryParseAlias(out EFieldConditions conditions)) return false;
        token.Conditions = conditions;
        return true;
    }


    public void NormalizeValue(FieldToken token)
    {
        if (token.Conditions == EFieldConditions.Is)
        {
            token.ConvertedValue = token.ValueStr;
            return;
        }

        if (token.Conditions == EFieldConditions.ExpLike)
        {
            token.ConvertedValue = token.ValueStr.ToEnPunctuation();
            return;
        }
        if (token.Conditions == EFieldConditions.NotLike)
        {
            token.Features |= EFieldConditionFeatures.Not;
            token.Conditions = EFieldConditions.Like;
        }
        if (token.Conditions == EFieldConditions.In && token.ValueStr.Contains(','))
        {
            token.Features |= EFieldConditionFeatures.Multi;
        }

        if (token.Conditions == EFieldConditions.Like && !token.FieldInfo!.FuzzSetting.IsNotSupported)
        {
            token.Features |= EFieldConditionFeatures.Fuzzy;
        }
        token.ConvertedValue = converter.ConvertEntrance(token.ValueStr, token.FieldInfo!.TypeSetting, token.Features);
        if (token.ConvertedValue is IList)
        {
            token.Features |= EFieldConditionFeatures.Multi;
        }
    }

    public NormalizedResult GenFinalExpression(TokenizerContext context)
    {
        var supplementObjects = new List<object>(); // Additional parameter values injected into generated Dynamic LINQ expressions.
        foreach (var (index, token) in context.Tokens.WithIndex())
        {
            var fieldInfo = token.FieldInfo!;
            token.TokenExpression = fieldInfo.BlendNavigationParam(gen.GenerateTokenExpression(token, index, context.Tokens.Count, out var supplementParamObjects));
            if (token.Features.HasTheFlag(EFieldConditionFeatures.Not))
            {
                token.TokenExpression = $"!({token.TokenExpression})";
            }

            if (token.Conditions.HasTheFlag(EFieldConditions.ExpLike))
            {
                token.TokenExpression = $"({token.TokenExpression})";
            }
            supplementObjects.AddRange(supplementParamObjects);

        }

        var parameters = context.Tokens.Select(p => p.ConvertedValue).ToList();
        parameters.AddRange(supplementObjects);
        return new NormalizedResult(context.GetFinalExpression(), parameters, context);
    }


    /// <summary>
    /// Returns the supported literal values for the specified enum type.
    /// </summary>
    /// <param name="type">The enum type to inspect.</param>
    /// <returns>A comma-separated list of supported values.</returns>
    private static string GetEnumRange(Type type)
    {
        return Enum.GetValues(type).Cast<Enum>()
            .Select(p => p.GetEnumAlias()).Where(p => p.IsNotNullOrEmpty())
            .StringJoin(',');
    }

    /// <summary>
    /// Regular expression used to tokenize filter expressions.
    /// </summary>
    /// <returns>The compiled tokenizer regex.</returns>
    [GeneratedRegex("""
                    (?<Field>[^(\s]+) (?<Condition>[\S]+) "(?<Value>.*?)"
                    """, RegexOptions.Compiled)]
    private static partial Regex ExpressionRegex();
}
