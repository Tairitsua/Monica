namespace Monica.Core.Configuration;

/// <summary>Identifies an original provider without retaining a mutable provider as the source of frozen values.</summary>
public sealed record ConfigurationProviderOrigin
{
    /// <summary>Gets the original provider's CLR type.</summary>
    public required Type ProviderType { get; init; }

    /// <summary>Gets its flattened priority, ordered from lowest to highest.</summary>
    public required int PriorityIndex { get; init; }

    /// <summary>Gets the original provider-index path through any chained roots, such as <c>2/0</c>.</summary>
    public required string ProviderPath { get; init; }

    /// <summary>Gets the provider's configured file path, which may be normalized, or null for a non-file provider.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Gets the resolved physical file path when available.</summary>
    public string? PhysicalPath { get; init; }

    /// <summary>Gets whether the original file was optional; null denotes a non-file provider.</summary>
    public bool? Optional { get; init; }

    /// <summary>Gets whether the original file watched for changes; the frozen snapshot itself never reloads.</summary>
    public bool? ReloadOnChange { get; init; }
}
