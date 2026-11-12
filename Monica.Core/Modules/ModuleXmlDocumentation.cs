using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Core.XmlDocumentation.Abstractions;
using Monica.Core.XmlDocumentation.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleXmlDocumentationBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the XML documentation module.
        /// </summary>
        public ModuleRegistration<ModuleXmlDocumentation, ModuleXmlDocumentationOption> AddXmlDocumentation(
            Action<ModuleXmlDocumentationOption>? action = null)
        {
            return builder.AddModule<ModuleXmlDocumentation, ModuleXmlDocumentationOption>(action);
        }
    }
}

/// <summary>
/// Provides XML documentation lookup and cache inspection endpoints.
/// </summary>
public class ModuleXmlDocumentation : MonicaModule<ModuleXmlDocumentationOption>, IWebModule
{
    /// <summary>
    /// Configures services.
    /// </summary>
    /// <param name="context">The module-owned service registration context.</param>
    public override void ConfigureServices(ModuleContext<ModuleXmlDocumentationOption> context)
    {
        context.Services.AddSingleton<IXmlDocumentationService, XmlDocumentationService>();
    }

    /// <summary>
    /// Configures endpoints.
    /// </summary>
    /// <param name="context">The module-owned Web application context.</param>
    public override void ConfigureEndpoints(WebModuleContext<ModuleXmlDocumentationOption> context)
    {
        if (!Option.EnableEndpoints)
        {
            return;
        }

        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            // Return cached XML documentation details.
            endpoints.MapGet("/xml-docs/cache", ([FromServices] IXmlDocumentationService xmlService) =>
            {
                try
                {
                    var cachedDocs = xmlService.GetCachedDocuments();
                    return Result.Success(cachedDocs.Select(doc => new
                    {
                        doc.AssemblyName,
                        doc.XmlFilePath,
                        doc.CachedAt,
                        HasDocument = doc.Document != null
                    })).GetResponse();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return Result.Failure($"Failed to get the XML documentation cache: {ex.Message}").GetResponse();
                }
            })
            .WithName("GetXmlDocumentationCache")
            .WithTags(tagName)
            .WithSummary("Gets the XML documentation cache")
            .WithDescription("Returns the assembly, file path, and cache state for each loaded XML documentation file.");

            // Clear the cached XML documentation.
            endpoints.MapPost("/xml-docs/cache/clear", ([FromServices] IXmlDocumentationService xmlService) =>
            {
                try
                {
                    xmlService.ClearCache();
                    return Result.Success().WithMessage("The XML documentation cache was cleared.").GetResponse();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return Result.Failure($"Failed to clear the XML documentation cache: {ex.Message}").GetResponse();
                }
            })
            .WithName("ClearXmlDocumentationCache")
            .WithTags(tagName)
            .WithSummary("Clears the XML documentation cache")
            .WithDescription("Evicts all cached XML documentation files from this Monica host.");
        });
    }
}

/// <summary>
/// Configures XML documentation inspection for one Monica host.
/// </summary>
public class ModuleXmlDocumentationOption : MinimalApiModuleOptions<ModuleXmlDocumentation>
{
    /// <summary>
    /// Enables the XML documentation cache endpoints exposed by this module.
    /// Leave this enabled when you need runtime inspection, or disable it to avoid exposing diagnostic endpoints.
    /// </summary>
    public bool EnableEndpoints { get; set; } = true;
}
