using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Monica.Core.Results;
using Monica.ModelQuery.Abstractions;
using Monica.Tool.Extensions;
using Monica.WebApi.Annotations;
using Monica.WebApi.AutoControllers.Abstractions;

namespace Monica.WebApi.AutoControllers.Services.Support;

/// <summary>Annotates published CRUD collection responses for OpenAPI while retaining the actual response types.</summary>
internal sealed class CrudApiDescriptionProvider : IApiDescriptionProvider
{
    private static readonly Type CrudServiceType = typeof(CrudService<,,,,>);

    // Run after MVC's default provider creates the response and parameter descriptions.
    public int Order => -999;

    public void OnProvidersExecuting(ApiDescriptionProviderContext context)
    {
    }

    public void OnProvidersExecuted(ApiDescriptionProviderContext context)
    {
        foreach (var description in context.Results)
        {
            if (description.ActionDescriptor is not ControllerActionDescriptor action ||
                !typeof(ICrudApplicationService).IsAssignableFrom(action.ControllerTypeInfo))
            {
                continue;
            }

            var responseType = description.SupportedResponseTypes.FirstOrDefault(response => response.StatusCode == 200)?.Type;
            var isStream = responseType == typeof(IAsyncEnumerable<object>);
            if (!isStream && responseType != typeof(Result<Page<object>>)) continue;

            var rowType = action.MethodInfo.GetCustomAttribute<CrudCollectionResponseAttribute>()?.RowType
                ?? FindInheritedRowType(action);
            if (rowType is null) continue;

            var supportsSelection = action.Parameters.Any(parameter =>
                typeof(IFieldSelectionRequest).IsAssignableFrom(parameter.ParameterType));
            description.Properties[typeof(CollectionResponse)] = new CollectionResponse(rowType, isStream, supportsSelection);
        }
    }

    private static Type? FindInheritedRowType(ControllerActionDescriptor action)
    {
        var declaringType = action.MethodInfo.GetBaseDefinition().DeclaringType;
        if (declaringType is null || !declaringType.IsDerivedFromGenericType(CrudServiceType)) return null;

        // Resolve the closed service even when an override comes through another generic base class.
        for (var type = action.ControllerTypeInfo.BaseType; type is not null; type = type.BaseType)
        {
            if (type.IsDerivedFromGenericType(CrudServiceType)) return type.GenericTypeArguments[3];
        }

        return null;
    }

    internal sealed record CollectionResponse(Type RowType, bool IsStream, bool SupportsSelection);
}
