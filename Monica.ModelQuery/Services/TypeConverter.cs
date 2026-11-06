using System.Collections;
using Microsoft.Extensions.Options;
using Monica.ModelQuery.Abstractions;
using Monica.ModelQuery.Exceptions;
using Monica.ModelQuery.Models;
using Monica.ModelQuery.Utils;
using Monica.Tool.Extensions;
using Monica.Tool.Text;

namespace Monica.ModelQuery.Services;

public class TypeConverter(IOptions<ModelQueryExpressionOptions> options) : IModelQueryValueConverter
{
    protected ModelQueryExpressionOptions Options = options.Value;

    // System.Dynamic.Linq does not support generic methods like Contains on List<object>; use strongly typed lists.
    private static IList ToStrongTypeList<T>(IEnumerable<T> enumerable)
    {
        var list = enumerable.ToList();
        if (list.FirstOrDefault()?.GetType() is { } type)
        {
            var listType = typeof(List<>).MakeGenericType(type);
            var typedList = (IList)Activator.CreateInstance(listType)!;
            foreach (var item in list)
            {
                typedList.Add(item);
            }
            return typedList;
        }
        return list;
    }

    public dynamic? ConvertEntrance(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if (features.HasTheFlag(EFieldConditionFeatures.Multi) && value.Contains(Options.FilterMultiSeparator))
        {
            return ToStrongTypeList(value.Split(Options.FilterMultiSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(singleValue => ConvertEntrance(singleValue, typeSetting, features)));
        }

        switch (typeSetting.BasicType)
        {
            case EBasicType.IsDouble:
                return ConvertDouble(value, typeSetting, features);
            case EBasicType.IsInt:
                return ConvertInt(value, typeSetting, features);
            case EBasicType.IsLong:
                return ConvertLong(value, typeSetting, features);
            case EBasicType.IsDecimal:
                return ConvertDecimal(value, typeSetting, features);
            case EBasicType.IsEnum:
                return ConvertEnum(value, typeSetting, features);
            case EBasicType.IsString:
                return ConvertString(value, typeSetting, features);
            case EBasicType.IsBoolean:
                return ConvertBoolean(value, typeSetting, features);
            case EBasicType.IsDateTime:
                return ConvertDateTime(value, typeSetting, features);
            case EBasicType.IsDateOnly:
                return ConvertDateOnly(value, typeSetting, features);
            case EBasicType.IsTimeOnly:
                return ConvertTimeOnly(value, typeSetting, features);
            case EBasicType.IsGuid:
                return ConvertGuid(value, typeSetting, features);
            case EBasicType.IsTimeSpan:
                return ConvertTimeSpan(value, typeSetting, features);
            case EBasicType.IsChar:
            case EBasicType.IsClass:
            case EBasicType.IsFloat:
            default: throw new ModelQueryValueConversionException(
                displayMessage: "数据类型转换失败",
                technicalDetail: $"不支持的类型: {typeSetting.OriginType.GetCleanFullName()}");
        }
    }


    public dynamic ConvertBoolean(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if (TextValueParser.TryToBool(value.ToLower(), out var boolResult))
        {
            return boolResult;
        }

        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            return value;
        }

        throw new ModelQueryValueConversionException(
            displayMessage: "布尔值转换失败",
            technicalDetail: $"无法转换 '{value}' 为 Boolean");
    }

    #region DateTime-related
    public dynamic ConvertTimeSpan(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            return value;
        }
        if (TimeSpan.TryParse(value, out var timeSpanResult))
        {
            return timeSpanResult;
        }

        throw new ModelQueryValueConversionException(
            displayMessage: "时间间隔转换失败",
            technicalDetail: $"无法转换 '{value}' 为 TimeSpan");
    }


    public dynamic ConvertDateTime(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            return value;
        }

        if (TemporalValueParser.TryToDateTime(value, out var dateTime))
        {
            return dateTime;
        }

        throw new ModelQueryValueConversionException(
            displayMessage: "日期时间转换失败",
            technicalDetail: $"无法转换 '{value}' 为 DateTime");
    }

    public dynamic ConvertTimeOnly(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            return value;
            // As of 2024-04-29, the PgSQL provider still does not support TimeOnly.ToString().
        }
        if (TemporalValueParser.TryToTimeOnly(value, out var time))
        {
            return time;
        }

        throw new ModelQueryValueConversionException(
            displayMessage: "时间转换失败",
            technicalDetail: $"无法转换 '{value}' 为 TimeOnly");
    }
    public dynamic ConvertDateOnly(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {

        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            return value;
            // As of 2024-04-29, the PgSQL provider still does not support DateOnly.ToString().
        }

        if (TemporalValueParser.TryToDateOnly(value, out var time))
        {
            return time;
        }

        throw new ModelQueryValueConversionException(
            displayMessage: "日期转换失败",
            technicalDetail: $"无法转换 '{value}' 为 DateOnly");
    }
    #endregion



    #region Numeric-related

    public dynamic ConvertDouble(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            return value;
        }

        if (double.TryParse(value, out var doubleResult))
        {
            return doubleResult;
        }

        throw new ModelQueryValueConversionException(
            displayMessage: "数值转换失败",
            technicalDetail: $"无法转换 '{value}' 为 Double");
    }

    public dynamic ConvertLong(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            return value;
        }
        if (long.TryParse(value, out var result))
        {
            return result;
        }
        throw new ModelQueryValueConversionException(
            displayMessage: "数值转换失败",
            technicalDetail: $"无法转换 '{value}' 为 Long");
    }

    public dynamic ConvertInt(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            return value;
        }
        if (int.TryParse(value, out var result))
        {
            return result;
        }
        throw new ModelQueryValueConversionException(
            displayMessage: "数值转换失败",
            technicalDetail: $"无法转换 '{value}' 为 Int32");
    }

    public dynamic ConvertDecimal(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            return value;
        }
        if (decimal.TryParse(value, out var result))
        {
            return result;
        }
        throw new ModelQueryValueConversionException(
            displayMessage: "数值转换失败",
            technicalDetail: $"无法转换 '{value}' 为 Decimal");
    }
    #endregion


    public dynamic ConvertEnum(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            EnumAliasParser.TryParseAlias(typeSetting.OriginUnderlyingType, value, out var enumResultList, true);
            return enumResultList is IList { Count: 0 }
                ? FieldResult.JumpThisField()
                : ((List<Enum>)enumResultList)
                .Select(p => Convert.ChangeType(p, typeSetting.OriginUnderlyingType))
                .ToList().ConvertToSpecificItemType(typeSetting.OriginUnderlyingType);
        }
        try
        {
            return Enum.Parse(typeSetting.OriginUnderlyingType, value, true);
        }
        catch (Exception e)
        {
            throw new ModelQueryValueConversionException(
                displayMessage: "枚举值转换失败",
                technicalDetail: e.Message);
        }
    }
    public dynamic ConvertString(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            return value.Contains('%') ? value : $"%{value}%";
        }
        return value;
    }
    public dynamic ConvertGuid(string value, QueryFieldTypeInfo typeSetting, EFieldConditionFeatures features)
    {
        if ((features & EFieldConditionFeatures.Fuzzy) != 0)
        {
            return value;
        }
        if (Guid.TryParse(value, out var guid))
        {
            return guid;
        }

        throw new ModelQueryValueConversionException(
            displayMessage: "唯一标识符转换失败",
            technicalDetail: $"无法转换 '{value}' 为 Guid");
    }


}
