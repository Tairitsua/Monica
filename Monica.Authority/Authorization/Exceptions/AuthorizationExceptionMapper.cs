using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Monica.Authority.Localization;
using Monica.Authority.Authorization.Services.Support;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.ExceptionHandling.Exceptions;
using Monica.Core.Results;

namespace Monica.Authority.Authorization.Exceptions;

internal class AuthorizationExceptionMapper(AuthorityMessageLocalizer authorityLocalizer) : IExceptionResponseMapper
{
    public bool TryMap(
        HttpContext? httpContext,
        Exception exception,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out Result? response)
    {
        switch (exception)
        {
            case BusinessException businessError:
                response = Result.Failure(businessError.Message);
                return true;

            case AuthorizationException { Type: AuthorizationException.ExceptionType.NotLogin }:
                response = ResultsAuthorization.NotLogin(authorityLocalizer);
                return true;

            case AuthorizationException { Type: AuthorizationException.ExceptionType.RefreshTokenExpired }:
                response = ResultsAuthorization.RefreshTokenExpired(authorityLocalizer);
                return true;

            case AuthorizationException { Type: AuthorizationException.ExceptionType.AccessTokenExpired }:
                response = ResultsAuthorization.AccessTokenExpired(authorityLocalizer);
                return true;

            case SecurityTokenExpiredException:
                response = ResultsAuthorization.AccessTokenExpired(authorityLocalizer);
                return true;

            case AuthorizationException authorizationException:
            {
                response = Result.Failure(
                        authorizationException.GetTitle(authorityLocalizer),
                        ResultStatus.Forbidden)
                    .SetError(new ResultError(ResultErrorCodes.Forbidden, ResultTraceId.Capture(httpContext)));
                return true;
            }

            case SecurityTokenArgumentException tokenMalformedException:
                response = Result.Failure(authorityLocalizer.GetTokenExceptionMessage(), ResultStatus.Unauthorized);
                return true;

            case SecurityTokenException:
                response = Result.Failure(authorityLocalizer.GetTokenExceptionMessage(), ResultStatus.Unauthorized);
                return true;

            default:
                response = null;
                return false;
        }
    }
}
