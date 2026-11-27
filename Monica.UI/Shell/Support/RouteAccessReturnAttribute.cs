namespace Monica.UI.Shell.Support;

/// <summary>Names a registered workspace to offer when access to the annotated page is denied.</summary>
/// <remarks>The shell evaluates the return route's access policy before showing its link.</remarks>
/// <param name="route">The app-local registered route, with or without a leading slash.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RouteAccessReturnAttribute(string route) : Attribute
{
    /// <summary>Gets the normalized target route.</summary>
    public string Route { get; } = route.Trim('/');
}
