using Monica.DataChannel.Pipeline;

namespace Monica.DataChannel.Abstractions;

/// <summary>
/// Defines the host-owned registration surface used to declare data-channel pipelines.
/// </summary>
/// <remarks>
/// The registrar belongs to one application host. Registrations are accepted only while
/// <see cref="IDataChannelSetup.Setup"/> is running. Automatic setups materialize before endpoint mapping;
/// manual setups materialize on explicit startup and cannot add startup-time HTTP mappings.
/// </remarks>
public interface IDataChannelRegistrar
{
    /// <summary>
    /// Adds a pipeline definition to the current application host.
    /// </summary>
    /// <param name="id">The unique channel identifier within the current host.</param>
    /// <param name="configure">A callback that configures the pipeline endpoints and middleware.</param>
    /// <param name="groupId">An optional group identifier used to organize related channels.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the identifier is already registered, the outer endpoint is missing,
    /// or the selected setup callback has completed.
    /// </exception>
    void Add(string id, Action<ChannelPipelineBuilder> configure, string? groupId = null);
}
