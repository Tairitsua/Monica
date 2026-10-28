using Monica.EntityFrameworkCore.Internal;

namespace Monica.EntityFrameworkCore.Models;

/// <summary>Immutable placement policy for explicitly marked infrastructure tables; business mappings are unchanged.</summary>
public sealed record InfrastructureTableLayout
{
    private InfrastructureTableLayout(string? preferredSchema) => PreferredSchema = preferredSchema;

    /// <summary>Uses the provider/context default namespace without adding a table prefix.</summary>
    public static InfrastructureTableLayout DefaultSchema { get; } = new(preferredSchema: null);

    /// <summary>Gets the preferred independent namespace, or null for the default namespace.</summary>
    public string? PreferredSchema { get; }

    /// <summary>Uses this schema on capable providers, otherwise the default namespace without a prefix.</summary>
    /// <remarks>Does not create a schema, move data, or fall back when database permissions are missing.</remarks>
    public static InfrastructureTableLayout PreferSchema(string schema)
    {
        TableLayoutIdentifiers.Validate(schema, nameof(schema));
        return new(schema);
    }
}
