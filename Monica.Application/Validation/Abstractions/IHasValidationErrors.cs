using System.ComponentModel.DataAnnotations;

namespace Monica.Application.Validation.Abstractions;

public interface IHasValidationErrors
{
    IList<ValidationResult> ValidationErrors { get; }
}
