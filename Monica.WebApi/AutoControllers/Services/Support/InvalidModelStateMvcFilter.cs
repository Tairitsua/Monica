using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.Results;
using Monica.Core.Results.Services;

namespace Monica.WebApi.AutoControllers.Services.Support;

/// <summary>Rejects invalid CRUD input before transactions, forwarding, or action execution.</summary>
/// <remarks>API controllers use their built-in ModelState filter and the same rejection factory.</remarks>
public sealed class InvalidModelStateMvcFilter(IRequestRejectionFactory rejections) : IActionFilter, IOrderedFilter
{
    /// <summary>Runs after ASP.NET's unsupported-content filter and before Monica's execution pipeline.</summary>
    public int Order => -1900;

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.Result is not null || context.ModelState.IsValid ||
            context.ActionDescriptor.EndpointMetadata.OfType<IApiBehaviorMetadata>().Any()) return;

        context.Result = ResultHttpProjection.ToMvcResult(
            rejections.FromModelState(context, ResultStatus.BadRequest).ToResult());
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context) { }
}
