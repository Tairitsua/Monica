namespace Monica.DataChannel.Abstractions;

/// <summary>
/// Contributes data-channel pipeline declarations to one application host.
/// </summary>
/// <remarks>
/// Register one or more singleton implementations with <c>UseSetup&lt;TSetup&gt;()</c>. Each setup type runs once
/// in registration order during <c>UseMonica()</c>, before any pipeline is materialized or initialized.
/// Channel identifiers must be unique across all contributors in the host.
/// </remarks>
public interface IDataChannelSetup
{
    /// <summary>
    /// Declares this contributor's pipelines in the current application host.
    /// </summary>
    /// <param name="channels">
    /// The host-owned registrar. It remains valid only until the framework materializes the registered pipelines.
    /// </param>
    void Setup(IDataChannelRegistrar channels);
}
