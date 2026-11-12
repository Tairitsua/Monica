using Microsoft.OpenApi;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Monica.WebApi.Swagger.Services.Support;

internal sealed class ResultEnvelopeOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var success = context.ApiDescription.SupportedResponseTypes.FirstOrDefault(response =>
            response.StatusCode is 200 or 201 && response.Type is not null &&
            typeof(IResultEnvelope).IsAssignableFrom(response.Type));
        if (success is null) return;
        operation.Responses ??= new OpenApiResponses();
        // Preserve endpoint-specific declarations and their success payloads.
        foreach (var status in new[] { 400, 401, 403, 404, 409, 413, 415, 429, 500, 502, 503, 504 })
        {
            operation.Responses.TryAdd(status.ToString(), new OpenApiResponse
            {
                Description = "Monica error envelope. Numeric application statuses retain their documented HTTP mapping.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = context.SchemaGenerator.GenerateSchema(typeof(Result), context.SchemaRepository)
                    }
                }
            });
        }
    }
}
