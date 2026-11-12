using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing.Patterns;
using Monica.WebApi.AutoControllers.ModelBinding;

namespace Monica.WebApi.AutoControllers.Services.Support;

/// <summary>
/// Adds the request property metadata that ASP.NET Core cannot infer for route placeholders bound
/// through Monica's composite endpoint request binder.
/// </summary>
/// <remarks>
/// The default API description provider sees the request payload and the route as separate inputs.
/// This provider joins them so OpenAPI consumers receive one strongly typed path parameter instead
/// of an untyped placeholder and a duplicate query parameter.
/// </remarks>
/// <param name="modelMetadataProvider">Provides metadata for request properties owned by route placeholders.</param>
public sealed class RequestEndpointApiDescriptionProvider(
    IModelMetadataProvider modelMetadataProvider) : IApiDescriptionProvider
{
    /// <summary>
    /// Runs after ASP.NET Core's default provider and Monica's CRUD description adjustments.
    /// </summary>
    public int Order => -998;

    /// <inheritdoc />
    public void OnProvidersExecuting(ApiDescriptionProviderContext context)
    {
        foreach (var description in context.Results)
        {
            EnrichRouteParameters(description);
        }
    }

    /// <inheritdoc />
    public void OnProvidersExecuted(ApiDescriptionProviderContext context)
    {
    }

    private void EnrichRouteParameters(ApiDescription description)
    {
        if (description.ActionDescriptor is not ControllerActionDescriptor actionDescriptor)
        {
            return;
        }

        var requestParameter = actionDescriptor.Parameters
            .OfType<ControllerParameterDescriptor>()
            .SingleOrDefault(static parameter =>
                parameter.ParameterInfo.GetCustomAttribute<ApiEndpointRequestAttribute>() is not null);
        if (requestParameter is null)
        {
            return;
        }

        var requestMetadata = modelMetadataProvider.GetMetadataForType(requestParameter.ParameterType);
        var route = actionDescriptor.AttributeRouteInfo?.Template ?? string.Empty;
        foreach (var placeholder in RoutePatternFactory.Parse(route).Parameters
                     .Select(static parameter => parameter.Name)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var property = requestMetadata.Properties.FirstOrDefault(metadata =>
                string.Equals(metadata.PropertyName, placeholder, StringComparison.OrdinalIgnoreCase));
            var pathParameter = description.ParameterDescriptions.FirstOrDefault(parameter =>
                parameter.Source == BindingSource.Path &&
                string.Equals(parameter.Name, placeholder, StringComparison.OrdinalIgnoreCase));
            if (property is null || pathParameter is null)
            {
                continue;
            }

            RemovePayloadDuplicate(description, requestParameter.ParameterType, property, pathParameter);

            pathParameter.Name = placeholder;
            pathParameter.Type = property.ModelType;
            pathParameter.ModelMetadata = property;
            pathParameter.ParameterDescriptor = requestParameter;
            pathParameter.BindingInfo = new BindingInfo
            {
                BinderModelName = placeholder,
                BindingSource = BindingSource.Path
            };
        }
    }

    private static void RemovePayloadDuplicate(
        ApiDescription description,
        Type requestType,
        ModelMetadata routeProperty,
        ApiParameterDescription pathParameter)
    {
        for (var index = description.ParameterDescriptions.Count - 1; index >= 0; index--)
        {
            var candidate = description.ParameterDescriptions[index];
            if (ReferenceEquals(candidate, pathParameter) ||
                candidate.ModelMetadata?.ContainerType != requestType ||
                !string.Equals(
                    candidate.ModelMetadata.PropertyName,
                    routeProperty.PropertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            description.ParameterDescriptions.RemoveAt(index);
        }
    }
}
