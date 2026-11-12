using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;

namespace Monica.Core.Execution.Mvc;

/// <summary>
/// Represents either an executed MVC action or an execution-pipeline short circuit.
/// </summary>
public sealed class MvcActionExecutionResult : IExecutionOutcome
{
    private MvcActionExecutionResult(
        IActionResult? result,
        ActionExecutedContext? executedContext)
    {
        Result = result;
        ExecutedContext = executedContext;
    }

    /// <summary>
    /// Gets the MVC result produced by the action or a short-circuiting behavior.
    /// </summary>
    public IActionResult? Result { get; }

    /// <summary>
    /// Gets the completed MVC context when the controller action ran, or <see langword="null"/> when a behavior
    /// short-circuited before action execution.
    /// </summary>
    public ActionExecutedContext? ExecutedContext { get; }

    /// <summary>Reports returned failures, including handled action exceptions, for diagnostics only.</summary>
    public bool IsFailure => ExecutedContext?.Exception is not null
        || Result is ObjectResult { Value: IResultEnvelope envelope } && envelope.Status is not (ResultStatus.Ok or ResultStatus.Created)
        || Result is JsonResult { Value: IResultEnvelope jsonEnvelope } && jsonEnvelope.Status is not (ResultStatus.Ok or ResultStatus.Created)
        || Result is IStatusCodeActionResult { StatusCode: >= 400 };

    /// <summary>
    /// Creates a result for a controller action that MVC executed.
    /// </summary>
    public static MvcActionExecutionResult FromExecutedAction(ActionExecutedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new MvcActionExecutionResult(context.Result, context);
    }

    /// <summary>
    /// Creates a result that skips controller-action execution and returns the supplied MVC result.
    /// </summary>
    public static MvcActionExecutionResult ShortCircuit(IActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new MvcActionExecutionResult(result, executedContext: null);
    }
}
