using System.ComponentModel.DataAnnotations;
using Monica.Rpc.Abstractions;

namespace Monica.Rpc.Models;


public record RpcRequest : IValidatableObject, IHasRpcHttpInfo
{
    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        yield break;
    }

    public Dictionary<string, string?>? Headers { get; set; }
}
