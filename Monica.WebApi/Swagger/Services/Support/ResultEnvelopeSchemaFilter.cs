using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Modules;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Monica.WebApi.Swagger.Services.Support;

internal sealed class ResultEnvelopeSchemaFilter(
    IOptions<ModuleResultEnvelopeOption> options,
    IJsonSerializerOptionsProvider serializer) : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(ResultError) && schema is OpenApiSchema errorSchema)
        {
            errorSchema.Required = new HashSet<string>(new[] { nameof(ResultError.Code), nameof(ResultError.TraceId) }
                .Select(name => serializer.SerializerOptions.PropertyNamingPolicy?.ConvertName(name) ?? name));
            return;
        }
        if (!typeof(IResultEnvelope).IsAssignableFrom(context.Type) || schema is not OpenApiSchema result) return;
        result.Properties ??= new Dictionary<string, IOpenApiSchema>();
        // Swagger does not apply JsonTypeInfo modifiers. Reflect the host's effective wire names here too.
        foreach (var property in serializer.SerializerOptions.GetTypeInfo(context.Type).Properties)
        {
            if (property.AttributeProvider is not MemberInfo member ||
                member.Name is not (nameof(Result.Status) or nameof(Result.Message) or nameof(Result.Metadata) or "Data")) continue;
            var originalName = member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                serializer.SerializerOptions.PropertyNamingPolicy?.ConvertName(member.Name) ?? member.Name;
            if (originalName == property.Name || !result.Properties.Remove(originalName, out var propertySchema)) continue;
            result.Properties[property.Name] = propertySchema;
            if (result.Required?.Remove(originalName) == true) result.Required.Add(property.Name);
        }
        result.Properties[options.Value.FieldNames.GetMetadataPropertyName(serializer.SerializerOptions)] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object | JsonSchemaType.Null,
            AdditionalPropertiesAllowed = true,
            Description = "Public application metadata. The reserved error member has one typed shape.",
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["error"] = context.SchemaGenerator.GenerateSchema(typeof(ResultError), context.SchemaRepository)
            }
        };
    }
}
