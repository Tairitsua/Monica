namespace Monica.WebApi.AutoControllers.Models;

/// <summary>
/// Configures generated CRUD controllers and their request conventions for one Monica host.
/// </summary>
public class CrudControllerOption
{
    /// <summary>The conventional CLR name prefix for generated CRUD resources.</summary>
    public const string DefaultServiceNamePrefix = "CrudService";

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
    /// Gets or sets the case-sensitive CLR name prefix removed to derive a CRUD resource name.
    /// Defaults to <c>CrudService</c>, so <c>CrudServiceAirport</c> exposes the resource <c>airport</c>.
    /// An empty prefix uses the full service name.
    /// </summary>
    /// <remarks>
    /// The marker interface determines discovery. Building Blocks uses this prefix as its naming convention
    /// when the host has not supplied an explicit CRUD rule.
    /// </remarks>
    public string ServiceNamePrefix { get; set; } = DefaultServiceNamePrefix;

    /// <summary>
    /// Gets the host-specific paging defaults and safety limits applied to generated CRUD requests.
    /// </summary>
    public AutoControllerPaginationOption Pagination { get; } = new();

    /// <summary>
    /// Gets the host-specific rules used to infer HTTP methods from action names.
    /// </summary>
    public ConventionalHttpMethodOption HttpMethods { get; } = new();
}
