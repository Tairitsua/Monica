namespace Monica.Configuration.Models;

/// <summary>
/// Identifies the logical service and concrete process publishing configuration definition metadata.
/// </summary>
public sealed record ConfigurationPublisherIdentity
{
    /// <summary>
    /// Gets the stable logical service key shared by replicas of the same service.
    /// </summary>
    /// <remarks>The value must not exceed 191 characters so every supported relational provider can index it.</remarks>
    public required string PublisherKey { get; init; }

    /// <summary>
    /// Gets the current process or replica identifier used for audit attribution.
    /// </summary>
    /// <remarks>The value must not exceed 450 characters.</remarks>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Gets the current host or replica display name.
    /// </summary>
    /// <remarks>The value must not exceed 450 characters.</remarks>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the publishing application version when available.
    /// </summary>
    /// <remarks>The value must not exceed 450 characters.</remarks>
    public string? Version { get; init; }
}

/// <summary>
/// Couples one local definition snapshot with the current service's reload-behavior evidence.
/// </summary>
public sealed record ConfigurationDefinitionPublication
{
    private ConfigurationDefinitionPublication()
    {
    }

    /// <summary>
    /// Gets the local definition snapshot.
    /// </summary>
    public required ConfigurationDefinition Definition { get; init; }

    /// <summary>
    /// Gets the current service's normalized reload-behavior observation.
    /// </summary>
    public required ConfigurationReloadBehaviorObservation ReloadBehaviorObservation { get; init; }

    /// <summary>
    /// Creates a publication from a local definition.
    /// </summary>
    public static ConfigurationDefinitionPublication FromDefinition(ConfigurationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Kind == ConfigurationKind.Startup)
            throw new InvalidOperationException("Startup configuration cannot be published to the managed metadata store.");
        return new ConfigurationDefinitionPublication
        {
            Definition = definition,
            ReloadBehaviorObservation = ConfigurationReloadBehaviorObservation.FromDefinition(definition)
        };
    }
}

/// <summary>
/// Represents a complete definition-publication snapshot from one logical service.
/// </summary>
/// <remarks>
/// Stores may remove earlier observations from the same publisher when their definitions are absent from this batch.
/// Callers must therefore include every definition currently known to the publishing service.
/// </remarks>
public sealed record ConfigurationDefinitionPublicationBatch
{
    private const int MAX_PUBLISHER_KEY_LENGTH = 191;
    private const int MAX_PUBLISHER_AUDIT_VALUE_LENGTH = 450;

    private ConfigurationDefinitionPublicationBatch()
    {
    }

    /// <summary>
    /// Gets the logical publisher and current process identity.
    /// </summary>
    public required ConfigurationPublisherIdentity Publisher { get; init; }

    /// <summary>
    /// Gets the complete publication snapshot.
    /// </summary>
    public required IReadOnlyList<ConfigurationDefinitionPublication> Publications { get; init; }

    /// <summary>Gets deployment-owned keys excluded from both publication and withdrawal of historical publisher state.</summary>
    public IReadOnlyList<string> ExcludedDefinitionKeys { get; init; } = [];

    /// <summary>
    /// Creates and validates a complete publication batch.
    /// </summary>
    /// <param name="publisher">The logical publisher and current process identity.</param>
    /// <param name="definitions">Every definition currently known to the publisher.</param>
    /// <returns>A normalized publication batch.</returns>
    /// <exception cref="ArgumentException">Publisher identity is incomplete or definition keys are duplicated.</exception>
    public static ConfigurationDefinitionPublicationBatch Create(
        ConfigurationPublisherIdentity publisher,
        IReadOnlyList<ConfigurationDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(definitions);
        RequireValue(publisher.PublisherKey, nameof(publisher.PublisherKey));
        RequireValue(publisher.InstanceId, nameof(publisher.InstanceId));
        RequireValue(publisher.Name, nameof(publisher.Name));
        var normalizedPublisher = publisher with
        {
            PublisherKey = publisher.PublisherKey.Trim(),
            InstanceId = publisher.InstanceId.Trim(),
            Name = publisher.Name.Trim(),
            Version = string.IsNullOrWhiteSpace(publisher.Version) ? null : publisher.Version.Trim()
        };
        RequireMaxLength(
            normalizedPublisher.PublisherKey,
            MAX_PUBLISHER_KEY_LENGTH,
            nameof(publisher.PublisherKey));
        RequireMaxLength(
            normalizedPublisher.InstanceId,
            MAX_PUBLISHER_AUDIT_VALUE_LENGTH,
            nameof(publisher.InstanceId));
        RequireMaxLength(
            normalizedPublisher.Name,
            MAX_PUBLISHER_AUDIT_VALUE_LENGTH,
            nameof(publisher.Name));
        if (normalizedPublisher.Version is not null)
        {
            RequireMaxLength(
                normalizedPublisher.Version,
                MAX_PUBLISHER_AUDIT_VALUE_LENGTH,
                nameof(publisher.Version));
        }

        var publications = definitions.Where(definition => definition.Kind == ConfigurationKind.Managed)
            .Select(ConfigurationDefinitionPublication.FromDefinition).ToArray();
        var duplicateKey = definitions
            .GroupBy(static definition => definition.DefinitionKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Skip(1).Any())?.Key;
        if (duplicateKey is not null)
        {
            throw new ArgumentException(
                $"The publisher supplied multiple definitions with key '{duplicateKey}'.",
                nameof(definitions));
        }

        return new ConfigurationDefinitionPublicationBatch
        {
            Publisher = normalizedPublisher,
            Publications = publications,
            ExcludedDefinitionKeys = definitions.Where(definition => definition.Kind == ConfigurationKind.Startup)
                .Select(definition => definition.DefinitionKey).ToArray()
        };
    }

    private static void RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Publisher identity values cannot be empty.", parameterName);
        }
    }

    private static void RequireMaxLength(string value, int maxLength, string parameterName)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException(
                $"Publisher identity value cannot exceed {maxLength} characters.",
                parameterName);
        }
    }
}
