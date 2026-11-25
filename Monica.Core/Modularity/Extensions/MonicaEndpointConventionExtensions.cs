using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Monica.Core.ApplicationRoles;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Core.Results.Services;

namespace Monica.Core.Modularity.Extensions;

/// <summary>
/// Provides endpoint-convention helpers for Monica-owned web endpoints.
/// </summary>
public static class MonicaEndpointConventionExtensions
{
    /// <summary>
    /// Marks an endpoint as Monica-owned so the host-level Monica endpoint policy can identify it.
    /// </summary>
    /// <typeparam name="TBuilder">Endpoint convention builder type.</typeparam>
    /// <param name="builder">Endpoint convention builder to configure.</param>
    /// <param name="kind">The Monica endpoint category.</param>
    /// <returns>The same endpoint convention builder.</returns>
    public static TBuilder WithMonicaEndpoint<TBuilder>(
        this TBuilder builder,
        MonicaEndpointKind kind = MonicaEndpointKind.MinimalApi)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithMetadata(new MonicaEndpointMetadata(kind));
        if (kind == MonicaEndpointKind.MinimalApi)
        {
            builder.WithMetadata(MonicaMinimalApiMetadata.Instance);
            builder.Finally(ApplyRoleAdmission);
        }

        return builder;
    }

    private static void ApplyRoleAdmission(EndpointBuilder endpoint)
    {
        if (endpoint.RequestDelegate is not { } next ||
            endpoint.ApplicationServices.GetService<IRoleHealthStateProvider>() is not { } health)
            return;
        var implementation = endpoint.Metadata.OfType<MethodInfo>().LastOrDefault()?.DeclaringType;
        var declaringOwner = endpoint.Metadata.OfType<MonicaEndpointOwner>().LastOrDefault();
        implementation ??= declaringOwner?.Module;
        if (implementation is null) return;
        var roleId = health.GetRoleId(implementation, declaringOwner?.RegistrationOwner ?? declaringOwner?.Module);
        if (roleId is null) return;
        // The final request delegate runs after authorization but before Minimal API binds service and body parameters.
        endpoint.RequestDelegate = context =>
        {
            if (!health.IsRoleFaulted(roleId)) return next(context);
            if (context.RequestServices.GetService<IResultErrorMessageProvider>() is not null)
                return ResultHttpProjection.ToMinimalApiResult(Result.Failure(RoleUnavailableException.MessageForRole(roleId),
                    ResultStatus.ServiceUnavailable)).ExecuteAsync(context);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        };
    }
}

internal sealed record MonicaEndpointOwner(Type Module, Type? RegistrationOwner);
