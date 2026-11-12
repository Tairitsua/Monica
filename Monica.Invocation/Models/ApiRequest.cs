using System.ComponentModel.DataAnnotations;
using Monica.Invocation.Abstractions;

namespace Monica.Invocation.Models;


/// <summary>An optional API request base carrying validation and HTTP caller headers.</summary>
/// <remarks>Application contracts that do not need HTTP headers can implement IResultRequest directly. Headers are transport metadata, not trusted identity.</remarks>
public record ApiRequest : IValidatableObject, IHasApiHttpInfo
{
    /// <summary>Returns request validation failures. The default request has no additional validation rules.</summary>
    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        yield break;
    }

    /// <inheritdoc />
    public Dictionary<string, string?>? Headers { get; set; }
}
