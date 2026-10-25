using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.OpenApi;
using Monica.Core.TypeDiscovery.Abstractions;
using SignalRSwaggerGen.Attributes;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Monica.SignalR.Services.Support;

/// <summary>Routes generated concrete-hub operations through the host's final Swagger inclusion policy.</summary>
internal sealed class SignalRSwaggerDocumentRoutingFilter(
    ITypeActivationPolicy activation,
    Func<string, ApiDescription, bool> shouldInclude) : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        // The package retains the reflected concrete class for inherited methods. Interface-only documentation
        // can lose its scanned root during inheritance traversal and does not identify a concrete hub owner.
        var hubType = context.MethodInfo.ReflectedType ?? context.MethodInfo.DeclaringType;
        if (hubType is null || !typeof(Hub).IsAssignableFrom(hubType)) return;

        var description = context.ApiDescription;
        description.ActionDescriptor.EndpointMetadata =
            [.. description.ActionDescriptor.EndpointMetadata, new HubMetadata(hubType), context.MethodInfo];

        // The package already applied this public per-hub selector. Express it as an explicit native group so
        // the final host predicate still has the opportunity to exclude the operation.
        if (hubType.GetCustomAttribute<SignalRHubAttribute>()?.DocumentNames is not null)
            description.GroupName = context.DocumentName;

        if (activation.GetActivation(hubType, TypeActivationKind.Endpoint) != ActivationMode.Disabled
            && shouldInclude(context.DocumentName, description)) return;

        // SignalRSwaggerGen adds the operation before invoking its public filters. Remove only that exact
        // operation; a filter must never remove an unrelated HTTP operation or a differently generated path.
        if (description.RelativePath is not { } path || description.HttpMethod is not { } method
            || !context.Document.Paths.TryGetValue(path, out var pathItem)
            || pathItem.Operations is not { } operations) return;

        var verb = new HttpMethod(method);
        if (operations.TryGetValue(verb, out var existing) && ReferenceEquals(existing, operation))
        {
            operations.Remove(verb);
            if (operations.Count == 0) context.Document.Paths.Remove(path);
        }
    }
}
