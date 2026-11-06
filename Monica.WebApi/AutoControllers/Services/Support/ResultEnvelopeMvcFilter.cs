using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Core.Results.Services;

namespace Monica.WebApi.AutoControllers.Services.Support;

/// <summary>
/// Keeps the HTTP status code aligned with the <see cref="Result" /> status code.
/// </summary>
public class ResultEnvelopeMvcFilter: IAlwaysRunResultFilter, IOrderedFilter
{
    /// <summary>Preserves framework rejection statuses before ApiController's ProblemDetails result filter.</summary>
    public int Order => -2100;

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is StatusCodeResult { StatusCode: 413 or 415 } rejection)
        {
            var status = (ResultStatus)rejection.StatusCode;
            var message = status == ResultStatus.PayloadTooLarge
                ? "The request payload is too large." : "The request content type is not supported.";
            context.Result = ResultHttpProjection.ToMvcResult(Result.Failure(message, status)
                .SetError(new ResultError(ResultErrorCodes.InvalidRequest, ResultTraceId.Capture(context.HttpContext))));
        }
        if (context.Result is ObjectResult { Value: IResultEnvelope response } result)
        {
            ResultHttpProjection.Prepare(response, context.HttpContext);
            result.StatusCode = ResultHttpProjection.GetStatusCode(response);
        }
        else if (context.Result is JsonResult { Value: IResultEnvelope jsonResponse } jsonResult)
        {
            ResultHttpProjection.Prepare(jsonResponse, context.HttpContext);
            jsonResult.StatusCode = ResultHttpProjection.GetStatusCode(jsonResponse);
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
        
    }
}
