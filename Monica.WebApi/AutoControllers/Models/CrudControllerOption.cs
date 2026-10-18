namespace Monica.WebApi.AutoControllers.Models;

/// <summary>
/// Configures generated CRUD controllers and their request conventions for one Monica host.
/// </summary>
public class CrudControllerOption
{
    /// <summary>
    /// Route prefix used for auto-generated CRUD endpoints.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>api/v1/[controller]</c>. With AddApplicationModel, <c>{domain}</c> expands to each
    /// concrete controller's owning domain. For example, <c>api/v1/{domain}</c> yields a distinct prefix per
    /// domain in a complete application host. An explicit controller Route attribute overrides this prefix.
    /// </remarks>
    public string RoutePath { get; set; } = "api/v1/[controller]";
    /// <summary>
    /// Class-name suffix stripped from a CRUD application service when deriving its controller (route) name.
    /// </summary>
    /// <remarks>
    /// This is purely a route-naming aid: a service such as <c>AircraftAppService</c> becomes the controller
    /// <c>Aircraft</c> and the route segment <c>aircraft</c>. It does not gate controller registration (that is decided
    /// by the <c>ICrudApplicationService</c> marker interface) and is not a naming-convention rule; name-convention
    /// enforcement lives in the project-unit system (<c>Monica.ProjectUnits</c>). If a service name does not end with this
    /// suffix, the name is simply left unchanged.
    /// </remarks>
    public string CrudControllerPostfix { get; set; } = "CrudService";

    /// <summary>
    /// Gets the host-specific paging defaults and safety limits applied to generated CRUD requests.
    /// </summary>
    public AutoControllerPaginationOption Pagination { get; } = new();

    /// <summary>
    /// Gets the host-specific rules used to infer HTTP methods from action names.
    /// </summary>
    public ConventionalHttpMethodOption HttpMethods { get; } = new();
}
