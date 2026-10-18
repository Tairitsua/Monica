namespace Monica.Invocation.Models;

/// <summary>The shared HTTP mapping used when a generated application call needs remote delivery.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="RouteTemplate">The complete relative route, including prefix and wire domain.</param>
/// <param name="IncludeQueryString">Whether non-route request properties belong in the query string.</param>
/// <param name="IncludeBody">Whether the request is serialized as JSON.</param>
public sealed record ApiHttpEndpoint(string Method, string RouteTemplate, bool IncludeQueryString, bool IncludeBody);
