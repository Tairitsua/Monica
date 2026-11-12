namespace Monica.Application.Annotations;

/// <summary>Declares an application operation and its shared HTTP mapping.</summary>
/// <param name="method">The HTTP method used when generating HTTP clients or receivers.</param>
/// <param name="route">The relative route after the shared route prefix and domain.</param>
/// <remarks>
/// Requests in PublishedLanguages.Domain{Domain}.Requests generate stable domain command/query interfaces.
/// Host generation configuration chooses client adapters and receiver publication independently of this declaration.
/// Command/query kind and domain follow the existing request-name and namespace conventions.
/// Generated method names are the request name with the Command or Query prefix removed.
/// Local clients use the same declaration without HTTP dispatch. HTTP metadata does not publish a receiver
/// or determine transaction semantics. Future transports use the operation identity rather than its HTTP route.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ApiEndpointAttribute(ApiHttpMethod method, string route) : Attribute
{
    /// <summary>Gets the HTTP method used by generated HTTP clients and receivers.</summary>
    public ApiHttpMethod Method { get; } = method;

    /// <summary>Gets the relative HTTP route. Leading/trailing slashes are not supported.</summary>
    public string Route { get; } = route;

    /// <summary>Gets or sets HTTP payload binding. Auto selects query binding for GET/DELETE and body binding otherwise.</summary>
    public ApiRequestBinding Binding { get; set; } = ApiRequestBinding.Auto;

}
