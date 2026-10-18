using Microsoft.CodeAnalysis;

namespace Monica.Generators.AutoController.Diagnostics;

internal static class DiagnosticDescriptors
{
    private const string CATEGORY = "RequestOwnedWebApi";

    public static readonly DiagnosticDescriptor LegacyHandlerAttribute = Create(
        "AC1002",
        "Handler contains endpoint metadata",
        "Handler '{0}' still declares MVC route, HTTP method, or request-binding attribute '{1}'. Move the contract to its request's [ApiEndpoint].");

    public static readonly DiagnosticDescriptor MissingConfiguration = Create(
        "AC1003",
        "Missing Web API generation configuration",
        "Assembly '{0}' must declare [assembly: WebApiGenerationConfig(\"route-prefix\", ...)] for request '{1}'.");

    public static readonly DiagnosticDescriptor InvalidConfiguration = Create(
        "AC1004",
        "Invalid Web API generation configuration",
        "Web API generation configuration for assembly '{0}' is invalid: {1}");

    public static readonly DiagnosticDescriptor InvalidPublishedNamespace = Create(
        "AC1005",
        "Invalid published request namespace",
        "Published endpoint request '{0}' must use the strict namespace pattern '*.PublishedLanguages.Domain{Domain}.Requests'.");

    public static readonly DiagnosticDescriptor InvalidRequestName = Create(
        "AC1006",
        "Invalid endpoint request name",
        "Endpoint request '{0}' must start with 'Command' or 'Query'.");

    public static readonly DiagnosticDescriptor InvalidResultContract = Create(
        "AC1007",
        "Invalid request result contract",
        "Endpoint request '{0}' must implement exactly one IRequest<TResult> contract; found {1}.");

    public static readonly DiagnosticDescriptor HandlerResultMismatch = Create(
        "AC1008",
        "Handler and request result contracts differ",
        "Handler '{0}' returns '{1}', but request '{2}' declares IRequest<{3}>.");

    public static readonly DiagnosticDescriptor DuplicateRoute = Create(
        "AC1009",
        "Duplicate endpoint route",
        "HTTP {0} route '{1}' is declared by multiple ApplicationService requests: {2}.");

    public static readonly DiagnosticDescriptor DuplicateOperation = Create(
        "AC1010",
        "Duplicate API operation",
        "API operation '{0}' is declared more than once in domain '{1}' {2} API: {3}.");

    public static readonly DiagnosticDescriptor InvalidRoute = Create(
        "AC1011",
        "Invalid endpoint route",
        "Endpoint request '{0}' declares invalid relative route '{1}': {2}");

    public static readonly DiagnosticDescriptor MissingRouteProperty = Create(
        "AC1012",
        "Route placeholder has no request property",
        "Route placeholder '{{{0}}}' has no matching readable public property on request '{1}'.");

    public static readonly DiagnosticDescriptor UnsupportedPublishedBinding = Create(
        "AC1013",
        "Published endpoint binding is not supported",
        "Published API request '{0}' cannot use {1} binding. Move transport-specific endpoints beside their handlers as local HTTP contracts.");

    public static readonly DiagnosticDescriptor MissingSummary = Create(
        "AC1014",
        "Endpoint request has no XML summary",
        "Endpoint request '{0}' must declare a non-empty XML <summary> used by generated APIs.");

    public static readonly DiagnosticDescriptor UnsupportedPublishedResult = Create(
        "AC1015",
        "Published endpoint result is not supported",
        "Published API request '{0}' returns raw object. Move download or transport-specific endpoints beside their handlers as local HTTP contracts.");

    public static readonly DiagnosticDescriptor InvalidOperationName = Create(
        "AC1016",
        "Invalid API operation name",
        "Endpoint request '{0}' resolves to invalid API operation name '{1}'. Rename the request so the suffix after Command or Query is a valid C# identifier.");

    public static readonly DiagnosticDescriptor InvalidPublishedResultEnvelope = Create(
        "AC1017",
        "Published endpoint result envelope is invalid",
        "Published API request '{0}' returns '{1}', which must be a concrete reference type implementing IRemoteResultEnvelope<TSelf> for its own type.");

    public static readonly DiagnosticDescriptor UnbindableRouteProperty = Create(
        "AC1018",
        "Route property cannot receive the route value",
        "Route placeholder '{{{0}}}' matches property '{1}' on request '{2}', but the property must expose a public setter/init accessor or belong to the request record constructor.");

    public static readonly DiagnosticDescriptor InvalidReceiverOwnership = Create(
        "AC1020",
        "Invalid receiver ownership",
        "Selected handler assembly '{0}' contains no concrete ApplicationService with an ApiEndpoint request. Select an implementation assembly, not a protocol assembly.");

    public static readonly DiagnosticDescriptor GenerationFailure = Create(
        "AC1099",
        "Request-owned API generation failed",
        "Request-owned API generation failed: {0}");

    public static readonly DiagnosticDescriptor InvalidApplicationDeclaration = Create(
        "AC1021",
        "Invalid application declaration",
        "Application ownership declaration for assembly '{0}' is invalid: {1}");

    public static readonly DiagnosticDescriptor DuplicateApplicationHandler = Create(
        "AC1022",
        "Application request has multiple handlers",
        "Application request '{0}' has multiple handler implementations: {1}.");

    public static readonly DiagnosticDescriptor MissingApplicationOwnership = Create(
        "AC1023",
        "Application handler has no role owner",
        "Published application handler '{0}' must belong to an assembly declaring ApplicationRole.");

    public static readonly DiagnosticDescriptor UndefinedApplicationDomain = Create(
        "AC1024",
        "Application role refers to an undefined domain",
        "Application role '{0}' declares domain '{1}', but no referenced assembly declares that ApplicationDomain.");

    public static readonly DiagnosticDescriptor ConflictingExecutionRole = Create(
        "AC1025",
        "Application operation requires a different role",
        "Application request '{0}' requires role '{1}', but its handler belongs to role '{2}'.");

    public static readonly DiagnosticDescriptor MissingApplicationOperation = Create(
        "AC1026",
        "Application API operation has no owned handler",
        "Published application request '{0}' belongs to a generated API but has no handler in a declared role assembly.");

    public static readonly DiagnosticDescriptor InvalidApplicationCatalog = Create(
        "AC1027",
        "Invalid application ownership catalog",
        "Application catalog is invalid: {0}");

    public static readonly DiagnosticDescriptor DeleteQueryBindingUnfit = Create(
        "AC1019",
        "DELETE endpoint binds a collection or complex request from the query string",
        "DELETE endpoint '{0}' resolves to query binding but declares collection or complex property '{1}'. " +
        "Prefer a POST bulk-delete route, or declare Binding = ApiRequestBinding.Body on [ApiEndpoint].",
        DiagnosticSeverity.Warning);

    private static DiagnosticDescriptor Create(
        string id,
        string title,
        string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        return new DiagnosticDescriptor(
            id,
            title,
            message,
            CATEGORY,
            severity,
            isEnabledByDefault: true);
    }
}
