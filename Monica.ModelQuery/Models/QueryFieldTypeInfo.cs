using System.Collections;
using System.Text.Json.Serialization;
using Monica.ModelQuery.Exceptions;
using Monica.Tool.Extensions;

namespace Monica.ModelQuery.Models;

/// <summary>
/// Describes a field's CLR type, supported value classification, and collection or nullable features.
/// </summary>
public class QueryFieldTypeInfo
{
    /// <summary>
    /// Type that declares this field.
    /// </summary>
    [JsonIgnore] public Type? DeclaringType { get; set; }
    /// <summary>
    /// Original field type.
    /// </summary>
    [JsonIgnore] public Type OriginType { get; set; } = null!;

    /// <summary>
    /// Underlying field type after unwrapping nullable and <see cref="IEnumerable"/> wrappers.
    /// </summary>
    [JsonIgnore] public Type OriginUnderlyingType { get; set; } = null!;

    /// <summary>
    /// Detected type features.
    /// </summary>
    public ETypeFeatures TypeFeatures { get; set; }

    /// <summary>
    /// Basic type classification.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EBasicType BasicType { get; set; }

    /// <summary>
    /// Name of the underlying field type.
    /// </summary>
    public string TypeName => OriginUnderlyingType.Name;

    /// <summary>Classifies the supplied property type for model query processing.</summary>
    /// <param name="parameterType">The field's CLR type.</param>
    /// <param name="declaringType">The type that declares the property, if known.</param>
    /// <exception cref="ModelQueryUnsupportedFieldTypeException">The field type cannot be classified.</exception>
    public QueryFieldTypeInfo(Type parameterType, Type? declaringType)
    {
        DeclaringType = declaringType;
        AutoSetTypeInfo(parameterType);
    }

    /// <summary>
    /// Populates the type metadata from the supplied field type.
    /// </summary>
    /// <param name="parameterType">The field type to analyze.</param>
    private void AutoSetTypeInfo(Type parameterType)
    {
        OriginType = parameterType;
        BasicType = GetTypeClass(parameterType, out var underlyingType, out var features);
        TypeFeatures = features;
        OriginUnderlyingType = underlyingType;
    }

    /// <summary>
    /// Gets the basic type classification for the specified type.
    /// </summary>
    /// <param name="parameterType">The type to classify.</param>
    /// <param name="underlyingType">The extracted underlying type.</param>
    /// <param name="features">The detected type features.</param>
    /// <returns>The basic type classification.</returns>
    public static EBasicType GetTypeClass(Type? parameterType, out Type underlyingType, out ETypeFeatures features)
    {
        var origin = parameterType;
        if (origin == null) throw new Exception("传入的parameterType为null，无法判断SpecialKind");
        var originParameterTypeName = parameterType!.FullName;
        features = ETypeFeatures.None;
        if (typeof(IEnumerable).IsAssignableFrom(parameterType) && parameterType != typeof(string))
        {
            features |= ETypeFeatures.IsCollection;
            if (parameterType.IsGenericType)
            {
                parameterType = parameterType.GetGenericArguments().FirstOrDefault();
            }
            else if (parameterType.IsArray)
            {
                parameterType = parameterType.GetElementType();
            }

            if (parameterType == null)
                throw new ModelQueryUnsupportedFieldTypeException(
                    displayMessage: "字段类型不支持",
                    technicalDetail: $"IEnumerable 泛型参数为 null: {originParameterTypeName}",
                    origin);
        }

        if (parameterType.IsDerivedFromGenericType(typeof(ThreadLocal<>)))
        {
            features |= ETypeFeatures.IsThreadLocal;
            parameterType = parameterType.GetGenericArguments().FirstOrDefault();
            if (parameterType == null)
                throw new ModelQueryUnsupportedFieldTypeException(
                    displayMessage: "字段类型不支持",
                    technicalDetail: $"ThreadLocal 泛型参数为 null: {originParameterTypeName}",
                    origin);
        }
        if (parameterType.IsNullableValueType())
        {
            features |= ETypeFeatures.IsNullable;
            parameterType = Nullable.GetUnderlyingType(parameterType)!;
        }
        if (parameterType is { IsClass: true, IsGenericType: true } && parameterType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            features |= ETypeFeatures.IsNullable;
            parameterType = Nullable.GetUnderlyingType(parameterType)!;
        }


        underlyingType = parameterType;
        if (underlyingType.IsEnum)
        {
            return EBasicType.IsEnum;
        }


        if (underlyingType == typeof(string)) return EBasicType.IsString;
        if (underlyingType == typeof(bool)) return EBasicType.IsBoolean;
        if (underlyingType == typeof(Guid)) return EBasicType.IsGuid;
        if (underlyingType == typeof(DateTime)) return EBasicType.IsDateTime;
        if (underlyingType == typeof(TimeSpan)) return EBasicType.IsTimeSpan;
        if (underlyingType == typeof(TimeOnly)) return EBasicType.IsTimeOnly;
        if (underlyingType == typeof(DateOnly)) return EBasicType.IsDateOnly;



        if (underlyingType == typeof(double))
        {
            features |= ETypeFeatures.IsNumeric;
            return EBasicType.IsDouble;
        }

        if (underlyingType == typeof(float))
        {
            features |= ETypeFeatures.IsNumeric;
            return EBasicType.IsFloat;
        }

        if (underlyingType == typeof(int))
        {
            features |= ETypeFeatures.IsNumeric;
            return EBasicType.IsInt;
        }

        if (underlyingType == typeof(long))
        {
            features |= ETypeFeatures.IsNumeric;
            return EBasicType.IsLong;
        }

        if (underlyingType == typeof(decimal))
        {
            features |= ETypeFeatures.IsNumeric;
            return EBasicType.IsDecimal;
        }


        if (underlyingType == typeof(char)) return EBasicType.IsChar;
        if (underlyingType.IsClass) return EBasicType.IsClass; // string is also a class.
        throw new ModelQueryUnsupportedFieldTypeException(
            displayMessage: "字段类型不支持",
            technicalDetail: $"不支持的类型: {underlyingType.GetCleanFullName()}",
            origin);
    }

    public override string ToString()
    {
        return
            $"{BasicType}{TypeFeatures.GetFlagsString(ignoreEnums: ETypeFeatures.None).BeIfNotEmpty(" and {0}", true)}{DeclaringType?.GetCleanFullName().Be("(from {0})", true)}";
    }
}

[Flags]
public enum ETypeFeatures
{
    None,
    IsCollection = 1 << 0,
    IsNullable = 1 << 1,
    IsThreadLocal = 1 << 2,
    IsNumeric = 1 << 3,
    IsClass = 1 << 5,
}

public enum EBasicType
{
    IsInt,
    IsDouble,
    IsLong,
    IsEnum,
    IsString,
    IsBoolean,
    IsDateTime,
    IsGuid,
    IsTimeSpan,
    IsChar,
    IsClass,
    IsFloat,
    IsTimeOnly,
    IsDateOnly,
    IsDecimal,
}
