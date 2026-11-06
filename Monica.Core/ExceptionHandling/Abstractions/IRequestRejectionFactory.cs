using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Monica.Core.ExceptionHandling.Models;
using Monica.Core.Results;

namespace Monica.Core.ExceptionHandling.Abstractions;

/// <summary>Normalizes request failures without exposing formatter exception messages.</summary>
public interface IRequestRejectionFactory
{
    /// <summary>Creates bounded field errors with the standard 400 rejection status.</summary>
    RequestRejection FromModelState(ActionContext context, ResultStatus status = ResultStatus.BadRequest);
    /// <summary>Preserves HTTP 413/415 and extracts safe JSON-path information.</summary>
    RequestRejection FromBadRequest(HttpContext? context, BadHttpRequestException exception);
    /// <summary>Normalizes declared validation errors into the standard 400 validation rejection.</summary>
    RequestRejection FromValidationErrors(HttpContext? context, IEnumerable<ValidationResult> errors);
}
