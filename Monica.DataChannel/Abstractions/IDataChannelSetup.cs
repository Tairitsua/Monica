namespace Monica.DataChannel.Abstractions;

/// <summary>
/// Contributes data-channel pipeline declarations to one application host.
/// </summary>
/// <remarks>
/// Register one or more singleton implementations with <c>UseSetup&lt;TSetup&gt;()</c>. Automatic setups declare
/// in registration order during <c>UseMonica()</c>, before their pipelines are materialized or initialized.
/// Manual setups remain unconstructed until explicitly started through <see cref="IDataChannelLifecycle"/>.
/// Successful declarations are retained across starts and restarts.
/// Channel identifiers must be unique across all contributors in the host.
/// </remarks>
public interface IDataChannelSetup
{
    /// <summary>
    /// Declares this contributor's pipelines in the current application host.
    /// </summary>
    /// <param name="channels">
    /// The host-owned registrar. It remains valid only during this synchronous callback.
    /// </param>
    void Setup(IDataChannelRegistrar channels);
}
