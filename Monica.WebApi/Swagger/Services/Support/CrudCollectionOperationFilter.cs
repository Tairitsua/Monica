using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.WebApi.AutoControllers.Services.Support;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Monica.WebApi.Swagger.Services.Support;

/// <summary>Describes CRUD array items without changing component schemas shared by other actions.</summary>
internal sealed class CrudCollectionOperationFilter(
    IOptions<ModuleResultEnvelopeOption> options,
    IJsonSerializerOptionsProvider serializer) : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!context.ApiDescription.Properties.TryGetValue(typeof(CrudApiDescriptionProvider.CollectionResponse), out var value) ||
            value is not CrudApiDescriptionProvider.CollectionResponse description ||
            operation.Responses?.TryGetValue("200", out var success) is not true || success.Content is null)
        {
            return;
        }

        var fullRow = context.SchemaGenerator.GenerateSchema(description.RowType, context.SchemaRepository);
        var items = description.SupportsSelection
            ? new OpenApiSchema
            {
                AnyOf =
                [
                    fullRow,
                    new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        AdditionalPropertiesAllowed = true,
                        Description = "The entity properties requested through field selection."
                    }
                ]
            }
            : fullRow;

        var jsonOptions = serializer.SerializerOptions;
        var dataName = options.Value.FieldNames.GetDataPropertyName(jsonOptions);
        var itemsName = jsonOptions.PropertyNamingPolicy?.ConvertName(nameof(Page<object>.Items)) ?? nameof(Page<object>.Items);
        foreach (var content in success.Content.Values)
        {
            if (content.Schema is not { } schema) continue;

            var arrayShape = new OpenApiSchema { Type = JsonSchemaType.Array, Items = items };
            if (description.IsStream)
            {
                content.Schema = WithItems(schema, items);
                continue;
            }

            // Preserve the generated envelope's status, metadata, payload, and paging fields. The
            // additional schema specifies only the action's item shape, which differs by resource.
            content.Schema = new OpenApiSchema
            {
                AllOf = [schema],
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    [dataName] = new OpenApiSchema
                    {
                        Properties = new Dictionary<string, IOpenApiSchema> { [itemsName] = arrayShape }
                    }
                }
            };
        }
    }

    private static IOpenApiSchema WithItems(IOpenApiSchema schema, IOpenApiSchema item)
    {
        if (schema.CreateShallowCopy() is OpenApiSchema array)
        {
            array.Items = item;
            return array;
        }

        return new OpenApiSchema { AllOf = [schema], Type = JsonSchemaType.Array, Items = item };
    }
}
