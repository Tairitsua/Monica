using System.ComponentModel.DataAnnotations;
using Monica.Core.Execution;

namespace Monica.Application.Validation;

/// <summary>Validates application inputs before persistence starts, for every mediator entry adapter.</summary>
public sealed class ApplicationValidationBehavior<TInput, TResult>(IServiceProvider services)
    : IExecutionBehavior<TInput, TResult>
{
    /// <inheritdoc />
    public Task<TResult> ExecuteAsync(ExecutionContext<TInput> context, ExecutionDelegate<TResult> next)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.Input is { } input)
        {
            var errors = new List<ValidationResult>();
            Validator.TryValidateObject(input, new ValidationContext(input, services, null), errors, validateAllProperties: true);
            if (errors.Count > 0)
            {
                throw new RequestValidationException(errors);
            }
        }

        return next();
    }
}
