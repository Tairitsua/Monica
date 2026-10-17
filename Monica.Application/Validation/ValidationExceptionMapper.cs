using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.Results;
using System.ComponentModel.DataAnnotations;
using Monica.Application.Validation.Annotations;

namespace Monica.Application.Validation;

internal class ValidationExceptionMapper(IRequestRejectionFactory rejections) : IExceptionResponseMapper
{
    public bool TryMap(
        HttpContext? httpContext,
        Exception exception,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out Res? response)
    {
        switch (exception)
        {
            case RequestValidationException validationException:
                response = rejections.FromValidationErrors(httpContext, Flatten(validationException.ValidationErrors)).ToResult();
                return true;
            default:
                response = null;
                return false;
        }
    }

    private static IEnumerable<ValidationResult> Flatten(IEnumerable<ValidationResult> results) =>
        results.SelectMany(result => result is NestedValidationResult nested
            ? Flatten(nested.NestedResults) : [result]);
}
