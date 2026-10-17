using System.Collections;
using System.ComponentModel.DataAnnotations;

namespace Monica.Application.Validation.Annotations;

/// <summary>
/// Represents a nested validation result.
/// </summary>
public class NestedValidationResult() : ValidationResult("One or more nested values are invalid.")
{
    /// <summary>Child errors with member paths relative to the containing request.</summary>
    public IList<ValidationResult> NestedResults { get; set; } = [];
}

/// <summary>
/// Continues validation into nested object types, including both collections and regular objects.
/// </summary>
public class ValidateChildrenAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null) return ValidationResult.Success;
        var visited = validationContext.Items.TryGetValue(VISITED_KEY, out var existing)
            ? (HashSet<object>)existing!
            : new HashSet<object>(ReferenceEqualityComparer.Instance);
        var errors = new List<ValidationResult>();
        var prefix = validationContext.MemberName ?? string.Empty;
        if (value is IEnumerable collection and not string)
        {
            var index = 0;
            foreach (var child in collection)
                Validate(child, $"{prefix}[{index++}]", validationContext, visited, errors);
        }
        else
        {
            Validate(value, prefix, validationContext, visited, errors);
        }

        return errors.Count == 0 ? ValidationResult.Success : new NestedValidationResult { NestedResults = errors };
    }

    private static readonly object VISITED_KEY = new();

    private static void Validate(object? value, string prefix, ValidationContext parent, HashSet<object> visited,
        List<ValidationResult> errors)
    {
        if (value is null || !visited.Add(value)) return;
        try
        {
            var items = new Dictionary<object, object?>(parent.Items) { [VISITED_KEY] = visited };
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(value, new ValidationContext(value, parent, items), results, true);
            foreach (var result in Flatten(results))
            {
                var members = result.MemberNames.DefaultIfEmpty(string.Empty)
                    .Select(member => string.IsNullOrEmpty(member) ? prefix : $"{prefix}.{member}");
                errors.Add(new ValidationResult(result.ErrorMessage, members));
            }
        }
        finally
        {
            visited.Remove(value);
        }
    }

    private static IEnumerable<ValidationResult> Flatten(IEnumerable<ValidationResult> results)
        => results.SelectMany(result => result is NestedValidationResult nested ? Flatten(nested.NestedResults) : [result]);
}
