namespace Monica.Generators.AutoController.Constants;

internal static class GeneratorConstants
{
    public const string API_ENDPOINT_ATTRIBUTE = "Monica.Application.Annotations.ApiEndpointAttribute";
    public const string APPLICATION_API_CONFIG_ATTRIBUTE = "Monica.Application.Annotations.ApplicationApiGenerationConfigAttribute";
    public const string WEB_API_CONFIG_ATTRIBUTE = "Monica.WebApi.Annotations.WebApiGenerationConfigAttribute";
    public const string HTTP_CONTRACT_ATTRIBUTE = "Monica.Application.Annotations.HttpApiContractAttribute";
    public const string APPLICATION_SERVICE_NAMESPACE = "Monica.Application.Abstractions";
    public const string GENERATED_CONTROLLER_NAMESPACE = "GeneratedControllers";
    public const string APPLICATION_HOST_ATTRIBUTE = "Monica.ApplicationModel.Annotations.ApplicationHostAttribute";
    public const string APPLICATION_DOMAIN_ATTRIBUTE = "Monica.ApplicationModel.Annotations.ApplicationDomainAttribute";
    public const string APPLICATION_ROLE_ATTRIBUTE = "Monica.ApplicationModel.Annotations.ApplicationRoleAttribute";
    public const string APPLICATION_REGISTRATION = "Monica.ApplicationModel.Abstractions.ApplicationRegistration";
    public const string RUN_ON_ROLE_ATTRIBUTE = "Monica.ApplicationModel.Annotations.RunOnRoleAttribute";

    public const int BINDING_AUTO = 0;
    public const int BINDING_QUERY = 1;
    public const int BINDING_BODY = 2;
    public const int BINDING_FORM = 3;

    public const int METHOD_GET = 0;
    public const int METHOD_POST = 1;
    public const int METHOD_PUT = 2;
    public const int METHOD_PATCH = 3;
    public const int METHOD_DELETE = 4;
}
