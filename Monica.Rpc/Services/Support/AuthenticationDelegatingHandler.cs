using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Monica.Authority.Identity.Abstractions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Adds the current user or system-user bearer token to outbound RPC HTTP requests.
/// </summary>
internal sealed class AuthenticationDelegatingHandler(
    IHttpContextAccessor httpContextAccessor,
    ISystemUserManager systemUserManager) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = httpContextAccessor.HttpContext;

        if (context != null)
        {
            if (context.Request.Headers.Authorization is { } authorization && !string.IsNullOrWhiteSpace(authorization.ToString()))
            {
                var authorizationValue = authorization.ToString();
                if (AuthenticationHeaderValue.TryParse(authorizationValue, out var parsedAuthorization))
                {
                    request.Headers.Authorization = parsedAuthorization;
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorizationValue.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (await context.GetTokenAsync("access_token") is { } token && !string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
        else
        {
            var token = systemUserManager.GetTokenOfCurSystemUser();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
