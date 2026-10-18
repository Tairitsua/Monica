namespace Platform.Protocol.Application;

/// <summary>Stable application responsibility identifiers, independent of project names and AppIds.</summary>
public static class ReferenceRoles
{
    /// <summary>Owns ordinary order commands and collection queries.</summary>
    public const string Ordering = "ordering";

    /// <summary>Owns order reporting queries and the autonomous backlog report.</summary>
    public const string OrderingReporting = "ordering.reporting";
}
