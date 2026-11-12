using System.Dynamic;

namespace Monica.Core.Results.Abstractions;

/// <summary>
/// Represents the shared envelope contract for Monica result models.
/// </summary>
public interface IResultEnvelope
{
    /// <summary>
    /// Gets or sets the response message.
    /// </summary>
    string? Message { get; set; }

    /// <summary>
    /// Gets or sets the result status.
    /// </summary>
    ResultStatus Status { get; set; }

    /// <summary>
    /// Gets or sets additional debug metadata such as chain-tracing details.
    /// </summary>
    ExpandoObject? Metadata { get; set; }
}
