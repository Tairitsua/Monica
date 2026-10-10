using Monica.Configuration.Models;

namespace Monica.Configuration.UI.Models;

/// <summary>Returns an analyzed editor draft and the paths whose existing staged state must be preserved.</summary>
internal sealed record ConfigurationJsonEditResult(
    ConfigurationJsonDraftResult Draft,
    IReadOnlyList<LogicalPath> RedactedPaths);
