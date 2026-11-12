using Monica.ApplicationModel.Models;

namespace Monica.Invocation.Models;

/// <summary>Trusted compiled ownership and the current request supplied to product authority policy.</summary>
/// <param name="Request">The current request; its payload is not an authority or authentication source.</param>
/// <param name="Operation">The validated owning operation.</param>
/// <param name="HandlerType">The verified local implementation, or null when only the shared contract is included in this executable.</param>
/// <remarks>Routing and authority rules must remain available from shared contracts for remote-only operations. Null must not silently disable a product policy.</remarks>
public sealed record ApplicationInvocationAuthorityContext(object Request, ApplicationOperationDefinition Operation, Type? HandlerType);

/// <summary>An explicit logical destination in a startup-configured HTTP provider scope.</summary>
/// <param name="AppId">The logical destination AppId, independently of its resolved network address.</param>
/// <param name="ProviderKey">An authority provider key, or null for the ordinary API provider.</param>
public sealed record ApplicationInvocationDestination(string AppId, string? ProviderKey = null);
