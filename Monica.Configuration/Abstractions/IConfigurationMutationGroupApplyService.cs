using Monica.Configuration.Models;

namespace Monica.Configuration.Abstractions;

/// <summary>
/// Validates, persists, reloads, and reports one configuration mutation group.
/// </summary>
public interface IConfigurationMutationGroupApplyService
{
    /// <summary>Previews complete effective aggregates and independently observable provider adoption states without writes.</summary>
    /// <param name="request">The proposed commands and persistence targets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete-group validation preview and its review fingerprint.</returns>
    Task<ConfigurationMutationGroupValidationPreview> PreviewAsync(
        ConfigurationMutationGroupApplyRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies one reviewed mutation group.
    /// </summary>
    /// <param name="request">The group request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The group outcome, including post-commit issues.</returns>
    Task<ConfigurationMutationGroupApplyResult> ApplyAsync(
        ConfigurationMutationGroupApplyRequest request,
        CancellationToken cancellationToken);
}
