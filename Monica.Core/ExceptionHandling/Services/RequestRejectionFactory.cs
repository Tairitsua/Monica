using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.ExceptionHandling.Models;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;

namespace Monica.Core.ExceptionHandling.Services;

internal sealed class RequestRejectionFactory(IJsonSerializerOptionsProvider jsonOptions, IResultErrorMessageProvider messages) : IRequestRejectionFactory
{
    private const int MAX_FIELDS = 32;
    private const int MAX_PATH_LENGTH = 256;
    private const int MAX_MESSAGE_LENGTH = 256;

    public RequestRejection FromModelState(ActionContext context, ResultStatus status = ResultStatus.BadRequest)
    {
        var entries = context.ModelState.Where(pair => pair.Value is { Errors.Count: > 0 }).ToArray();
        if (entries.Any(pair => pair.Value!.Errors.Any(error => error.Exception is UnsupportedContentTypeException)))
            status = ResultStatus.UnsupportedMediaType;

        var bodyParameters = context.ActionDescriptor.Parameters
            .Where(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Body)
            .Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fields = entries.Where(pair => entries.Length == 1 || !bodyParameters.Contains(pair.Key))
            .Take(MAX_FIELDS)
            .Select(pair => CreateField(context, pair.Key, pair.Value!))
            .ToArray();
        return Create(context.HttpContext, status, ResultErrorCodes.InvalidRequest, fields);
    }

    public RequestRejection FromBadRequest(HttpContext? context, BadHttpRequestException exception)
    {
        var status = exception.StatusCode switch
        {
            StatusCodes.Status413PayloadTooLarge => ResultStatus.PayloadTooLarge,
            StatusCodes.Status415UnsupportedMediaType => ResultStatus.UnsupportedMediaType,
            _ => ResultStatus.BadRequest
        };
        var fields = exception.InnerException is JsonException jsonException
            ? new[] { new ResultFieldError(NormalizePath(jsonException.Path ?? ""), "invalid_format", "Enter a valid value.") }
            : [];
        return Create(context, status, ResultErrorCodes.InvalidRequest, fields);
    }

    public RequestRejection FromValidationErrors(HttpContext? context, IEnumerable<ValidationResult> errors)
    {
        var fields = errors.SelectMany(error => error.MemberNames.DefaultIfEmpty("")
                .Select(member => new ResultFieldError(NormalizePath(member), "invalid",
                    Limit(string.IsNullOrWhiteSpace(error.ErrorMessage) ? "The value did not pass validation." : error.ErrorMessage, MAX_MESSAGE_LENGTH))))
            .Take(MAX_FIELDS).ToArray();
        return Create(context, ResultStatus.BadRequest, ResultErrorCodes.ValidationFailed, fields);
    }

    private RequestRejection Create(HttpContext? context, ResultStatus status, string code, ResultFieldError[] fields)
    {
        var error = new ResultError(code, ResultTraceId.Capture(context), fields: fields);
        var message = status switch
        {
            ResultStatus.PayloadTooLarge => "The request payload is too large.",
            ResultStatus.UnsupportedMediaType => "The request content type is not supported.",
            _ => messages.GetMessage(error)
        };
        return new RequestRejection(status, message, error);
    }

    private ResultFieldError CreateField(ActionContext context, string path, ModelStateEntry entry)
    {
        var parts = path.TrimStart('$', '.').Split('.');
        Type? type = null;
        var parameter = context.ActionDescriptor.Parameters.FirstOrDefault(parameter => parameter.Name == parts[0]);
        if (parameter is not null)
        {
            type = parameter.ParameterType;
            if (parts.Length > 1) parts = parts[1..];
        }
        else
        {
            type = context.ActionDescriptor.Parameters.Select(parameter => parameter.ParameterType)
                .FirstOrDefault(candidate => FindProperty(candidate, parts[0]) is not null);
        }
        for (var index = 0; index < parts.Length && type is not null; index++)
        {
            var bracket = parts[index].IndexOf('[');
            var member = bracket < 0 ? parts[index] : parts[index][..bracket];
            var property = FindProperty(type, member);
            if (property is null) break;
            parts[index] = property.Name + (bracket < 0 ? "" : parts[index][bracket..]);
            type = property.PropertyType;
            if (bracket >= 0)
                type = type.GetElementType() ?? type.GetInterfaces()
                    .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    ?.GetGenericArguments()[0];
        }

        var isDateTime = (Nullable.GetUnderlyingType(type ?? typeof(object)) ?? type) == typeof(DateTime);
        var message = isDateTime
            ? "Enter a date and time, for example 2026-09-15 14:30:00."
            : entry.Errors.FirstOrDefault(error => error.Exception is null && !string.IsNullOrWhiteSpace(error.ErrorMessage))?.ErrorMessage
              ?? "Enter a valid value.";
        return new ResultFieldError(Limit(string.Join('.', parts), MAX_PATH_LENGTH),
            isDateTime ? "invalid_date_time" : "invalid_value", Limit(message, MAX_MESSAGE_LENGTH));
    }

    private JsonPropertyInfo? FindProperty(Type type, string name)
    {
        var metadata = jsonOptions.SerializerOptions.GetTypeInfo(type);
        return metadata.Kind == JsonTypeInfoKind.Object
            ? metadata.Properties.FirstOrDefault(property =>
                property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                property.AttributeProvider is MemberInfo member && member.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private string NormalizePath(string path)
    {
        var segments = path.TrimStart('$', '.').Split('.');
        for (var index = 0; index < segments.Length; index++)
        {
            var bracket = segments[index].IndexOf('[');
            var name = bracket < 0 ? segments[index] : segments[index][..bracket];
            var suffix = bracket < 0 ? "" : segments[index][bracket..];
            segments[index] = (jsonOptions.SerializerOptions.PropertyNamingPolicy?.ConvertName(name) ?? name) + suffix;
        }
        return Limit(string.Join('.', segments), MAX_PATH_LENGTH);
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
