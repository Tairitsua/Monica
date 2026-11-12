using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Monica.Core.Results;
using Monica.Tool.Extensions;
using Monica.WebApi.AutoControllers.Abstractions;
using Monica.WebApi.AutoControllers.Extensions;

namespace Monica.WebApi.AutoControllers.Services.Support;

// This provider only needs to be registered; ASP.NET Core automatically discovers and executes all registered providers.
public class CrudApiDescriptionProvider(IModelMetadataProvider modelMetadataProvider)
    : IApiDescriptionProvider
{
    public void OnProvidersExecuted(ApiDescriptionProviderContext context)
    {
    }

    /// <summary>
    /// The order -999 ensures that this provider is executed right after the
    /// Microsoft.AspNetCore.Mvc.ApiExplorer.DefaultApiDescriptionProvider.
    /// </summary>
    public int Order => -999;

    public void OnProvidersExecuting(ApiDescriptionProviderContext context)
    {
        // TODO: Add OpenAPI response descriptions.

        //IOptions<AbpRemoteServiceApiDescriptionProviderOptions> optionsAccessor

        //AbpAspNetCoreMvcModule.cs
        //Configure<AbpRemoteServiceApiDescriptionProviderOptions>(options =>
        //{
        //    var statusCodes = new List<int>
        //    {
        //        (int) HttpStatusCode.Forbidden,
        //        (int) HttpStatusCode.Unauthorized,
        //        (int) HttpStatusCode.BadRequest,
        //        (int) HttpStatusCode.NotFound,
        //        (int) HttpStatusCode.NotImplemented,
        //        (int) HttpStatusCode.InternalServerError
        //    };

        //    options.SupportedResponseTypes.AddIfNotContains(statusCodes.Select(statusCode => new ApiResponseType
        //    {
        //        Type = typeof(RemoteServiceErrorResponse),
        //        StatusCode = statusCode
        //    }));
        //});



        //foreach (var apiResponseType in GetApiResponseTypes())
        //{
        //    foreach (var result in context.Results.Where(x => x.IsRemoteService()))
        //    {
        //        var actionProducesResponseTypeAttributes =
        //            ReflectionHelper.GetAttributesOfMemberOrDeclaringType<ProducesResponseTypeAttribute>(
        //                result.ActionDescriptor.GetMethodInfo());
        //        if (actionProducesResponseTypeAttributes.Any(x => x.StatusCode == apiResponseType.StatusCode))
        //        {
        //            continue;
        //        }

        //        result.SupportedResponseTypes.AddIfNotContains(x => x.StatusCode == apiResponseType.StatusCode,
        //            () => apiResponseType);
        //    }
        //}

        UpdateDynamicListResponseTypeToExactType(context);
        return;
    }

    /// <summary>
    /// Ensures CRUD endpoints that expose dynamic response types still display their concrete DTO types in Swagger.
    /// </summary>
    /// <param name="context">The API description provider context.</param>
    protected virtual void UpdateDynamicListResponseTypeToExactType(ApiDescriptionProviderContext context)
    {
        foreach (var apiDescription in context.Results.Where(p=>p.ActionDescriptor.IsControllerAction()))
        {
            var controllerActionDescriptor = apiDescription.ActionDescriptor.AsControllerActionDescriptor();
            var type = controllerActionDescriptor.ControllerTypeInfo;

            if (!type.AsType().IsImplementInterface<ICrudApplicationService>())
            {
                continue;
            }

            if (!controllerActionDescriptor.ActionName.EqualsAny("List", "GetList"))
            {
                continue;
            }
           

            var finalBaseType = type.BaseType;
            while (finalBaseType is not null)
            {
                if (finalBaseType.IsDerivedFromGenericType(typeof(CrudApplicationService<,,,,,,,,>)))
                {
                    break;
                }

                finalBaseType = finalBaseType.BaseType;
            }
            if(finalBaseType is null) continue;

            if (finalBaseType.GenericTypeArguments.Length == 8 && apiDescription.SupportedResponseTypes.FirstOrDefault() is {StatusCode: 200} responseType)
            {
                var getListOutputDto = finalBaseType.GenericTypeArguments[2];
                responseType.Type = typeof(Result<>).MakeGenericType(getListOutputDto);
                responseType.ModelMetadata = modelMetadataProvider.GetMetadataForType(responseType.Type);

            }
        }
    }

    //protected virtual IEnumerable<ApiResponseType> GetApiResponseTypes()
    //{
    //    foreach (var apiResponse in _options.SupportedResponseTypes)
    //    {
    //        apiResponse.ModelMetadata = modelMetadataProvider.GetMetadataForType(apiResponse.Type!);

    //        foreach (var responseTypeMetadataProvider in _mvcOptions.OutputFormatters.OfType<IApiResponseTypeMetadataProvider>())
    //        {
    //            var formatterSupportedContentTypes = responseTypeMetadataProvider.GetSupportedContentTypes(null!, apiResponse.Type!);
    //            if (formatterSupportedContentTypes == null)
    //            {
    //                continue;
    //            }

    //            foreach (var formatterSupportedContentType in formatterSupportedContentTypes)
    //            {
    //                apiResponse.ApiResponseFormats.Add(new ApiResponseFormat
    //                {
    //                    Formatter = (IOutputFormatter)responseTypeMetadataProvider,
    //                    MediaType = formatterSupportedContentType
    //                });
    //            }
    //        }
    //    }

    //    return _options.SupportedResponseTypes;
    //}
}
