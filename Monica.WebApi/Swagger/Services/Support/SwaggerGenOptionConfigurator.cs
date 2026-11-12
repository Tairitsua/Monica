using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Monica.Modules;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Tool.Extensions;
using Monica.WebApi.Swagger.Extensions;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Monica.WebApi.Swagger.Services.Support;

/// <summary>
/// Applies Monica's shared SwaggerGen configuration.
/// </summary>
internal static class SwaggerGenOptionConfigurator
{
    private const string JwtSecuritySchemeId = JwtBearerDefaults.AuthenticationScheme;
    private const string JwtBearerHttpScheme = "bearer";

    public static void Configure(
        SwaggerGenOptions options,
        ModuleSwaggerOption option,
        ILogger logger,
        SwaggerDocumentCatalog documentCatalog,
        ITypeFinder typeFinder)
    {
        ConfigureDocuments(options, documentCatalog);

        options.AddEnumDocumentation();

        // Pitfall: this API guards the visibility toggle in the Swagger UI's top-right group picker, and if omitted ABP (and our CrudAutoController-generated endpoints) will hide every controller.
        Func<string, ApiDescription, bool> routing = documentCatalog.ShouldInclude;
        options.DocInclusionPredicate(routing);

        // https://github.com/swagger-api/swagger-ui/issues/7911
        // https://github.com/microsoftgraph/msgraph-beta-sdk-dotnet/issues/285
        //
        // Pitfall: Swagger uses a type's full name for schema IDs by default, so anonymous types with unstable or duplicated names can collide when multiple methods return structurally identical payloads.
        // Pitfall: illegal characters also crash schema generation, so we filter them out. Replacing '+' with '.' is necessary; `.Replace("`", "_")` appears redundant.
        // Best practice is to expose explicit DTO types instead of anonymous results.
        options.CustomSchemaIds(type => type.GetCleanFullName());

        ConfigureXmlDocumentation(options, option, logger, typeFinder);

        if (option.UseAuth)
        {
            ConfigureJwtBearerSecurity(options);
        }

        option.ExtendSwaggerGenAction?.Invoke(options);
        SwaggerDocumentCatalog.ValidateDocumentNames(options.SwaggerGeneratorOptions.SwaggerDocs.Keys);
        var customizedPredicate = options.SwaggerGeneratorOptions.DocInclusionPredicate;
        if (!ReferenceEquals(customizedPredicate, routing))
        {
            // Host filters can narrow native documents, but must not mix role ownership across them. Documents
            // registered directly by the host remain outside Monica's routing and keep the host predicate.
            options.DocInclusionPredicate((documentName, description) =>
                routing(documentName, description) && customizedPredicate(documentName, description));
        }
    }

    private static void ConfigureDocuments(
        SwaggerGenOptions options,
        SwaggerDocumentCatalog documentCatalog)
    {
        foreach (var document in documentCatalog.Documents)
        {
            options.SwaggerDoc(document.Name, documentCatalog.GetOpenApiInfo(document));
        }
    }

    private static void ConfigureXmlDocumentation(
        SwaggerGenOptions options,
        ModuleSwaggerOption option,
        ILogger logger,
        ITypeFinder typeFinder)
    {
        if (option.DisableXmlDocumentation)
        {
            return;
        }

        var xmlFilePaths = SwaggerXmlDocumentationFileResolver.Resolve(option, logger, typeFinder);
        if (option.DisableInheritDocFilter)
        {
            foreach (var filePath in xmlFilePaths)
            {
                options.IncludeXmlComments(filePath);
            }

            return;
        }

        options.IncludeXmlCommentsWithInheritDoc(xmlFilePaths, includeControllerXmlComments: true, logger);
    }

    private static void ConfigureJwtBearerSecurity(SwaggerGenOptions options)
    {
        options.AddSecurityDefinition(JwtSecuritySchemeId, new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Description = "Enter JWT Bearer token **_only_**",
            In = ParameterLocation.Header,
            Type = SecuritySchemeType.Http,
            Scheme = JwtBearerHttpScheme,
            BearerFormat = "JWT"
        });

        options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(JwtSecuritySchemeId, document)] = []
        });
    }
}
