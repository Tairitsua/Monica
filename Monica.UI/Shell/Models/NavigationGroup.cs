namespace Monica.UI.Shell.Models;

/// <summary>
/// Represents one resolved navigation category with its ordered entries and nested categories.
/// </summary>
/// <param name="Id">The stable category identity.</param>
/// <param name="DisplayName">The localized category label for the current host.</param>
/// <param name="Items">The navigation entries in configured order.</param>
/// <param name="Children">The nonempty child categories in configured order.</param>
/// <param name="Icon">The optional category icon.</param>
/// <param name="Description">The resolved category description, or null when none was registered.</param>
public sealed record NavigationGroup(
    NavigationCategoryId Id,
    string DisplayName,
    IReadOnlyList<NavigationItem> Items,
    IReadOnlyList<NavigationGroup> Children,
    string? Icon = null,
    string? Description = null)
{
    /// <summary>
    /// Gets the number of authorized destinations in this category and all of its descendants.
    /// Empty or unauthorized categories are removed before the resolved tree is constructed.
    /// </summary>
    public int DestinationCount => Items.Count + Children.Sum(child => child.DestinationCount);
}
