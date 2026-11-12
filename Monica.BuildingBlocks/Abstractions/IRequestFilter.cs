namespace Monica.BuildingBlocks.Abstractions;

/// <summary>
/// Controls the host-local set of request paths that the Building Blocks middleware suppresses.
/// </summary>
public interface IRequestFilter
{
    /// <summary>
    /// Suppresses requests whose path exactly matches <paramref name="url"/>.
    /// </summary>
    /// <param name="url">The request path to suppress.</param>
    void Disable(string url);

    /// <summary>
    /// Removes a path from the suppression set.
    /// </summary>
    /// <param name="url">The request path to enable.</param>
    void Enable(string url);

    /// <summary>
    /// Gets a snapshot of the currently suppressed request paths.
    /// </summary>
    /// <returns>The suppressed request paths for the current host.</returns>
    List<string> GetDisabledUrls();
}
