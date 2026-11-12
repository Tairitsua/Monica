using System.Text.Json;
using Monica.Core.JsonSerialization.Services.Support;
using Monica.Core.Results.Models.Internal;
// ReSharper disable once CheckNamespace
namespace Monica.Core.Results;

/// <summary>
/// Configures top-level JSON field names for Monica result envelopes.
/// </summary>
public sealed class ResultEnvelopeFieldNames
{
    private const string MESSAGE_PROPERTY_NAME = nameof(Result.Message);
    private const string STATUS_PROPERTY_NAME = nameof(Result.Status);
    private const string DATA_PROPERTY_NAME = nameof(Result<>.Data);
    private const string METADATA_PROPERTY_NAME = nameof(Result.Metadata);

    /// <summary>
    /// Gets or sets the JSON field name used for <see cref="Result.Message" />.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the JSON field name used for <see cref="Result.Status" />.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Gets or sets the JSON field name used for top-level <c>Data</c> payload properties.
    /// </summary>
    public string? Data { get; set; }

    /// <summary>
    /// Gets or sets the JSON field name used for <see cref="Result.Metadata" />.
    /// </summary>
    public string? Metadata { get; set; }

    internal bool HasCustomizations =>
        Message is not null ||
        Status is not null ||
        Data is not null ||
        Metadata is not null;

    /// <summary>
    /// Gets the effective JSON field name used for <see cref="Result.Message" />.
    /// </summary>
    /// <param name="options">The serializer options used by the application.</param>
    /// <returns>The effective JSON field name.</returns>
    public string GetMessagePropertyName(JsonSerializerOptions options)
    {
        return GetEffectiveName(MESSAGE_PROPERTY_NAME, options);
    }

    /// <summary>
    /// Gets the effective JSON field name used for <see cref="Result.Status" />.
    /// </summary>
    /// <param name="options">The serializer options used by the application.</param>
    /// <returns>The effective JSON field name.</returns>
    public string GetStatusPropertyName(JsonSerializerOptions options)
    {
        return GetEffectiveName(STATUS_PROPERTY_NAME, options);
    }

    /// <summary>
    /// Gets the effective JSON field name used for top-level <c>Data</c> payload properties.
    /// </summary>
    /// <param name="options">The serializer options used by the application.</param>
    /// <returns>The effective JSON field name.</returns>
    public string GetDataPropertyName(JsonSerializerOptions options)
    {
        return GetEffectiveName(DATA_PROPERTY_NAME, options);
    }

    /// <summary>
    /// Gets the effective JSON field name used for <see cref="Result.Metadata" />.
    /// </summary>
    /// <param name="options">The serializer options used by the application.</param>
    /// <returns>The effective JSON field name.</returns>
    public string GetMetadataPropertyName(JsonSerializerOptions options)
    {
        return GetEffectiveName(METADATA_PROPERTY_NAME, options);
    }

    internal void ApplyTo(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!HasCustomizations)
        {
            return;
        }

        Validate(options);
        options.TypeInfoResolver = ResultEnvelopeJsonTypeInfoResolver.Create(this, options.GetConfiguredTypeInfoResolver());
    }

    internal void Validate(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ValidateConfiguredName(Message, nameof(Message));
        ValidateConfiguredName(Status, nameof(Status));
        ValidateConfiguredName(Data, nameof(Data));
        ValidateConfiguredName(Metadata, nameof(Metadata));

        var effectiveNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(Message)] = GetEffectiveName(MESSAGE_PROPERTY_NAME, options),
            [nameof(Status)] = GetEffectiveName(STATUS_PROPERTY_NAME, options),
            [nameof(Data)] = GetEffectiveName(DATA_PROPERTY_NAME, options),
            [nameof(Metadata)] = GetEffectiveName(METADATA_PROPERTY_NAME, options)
        };

        var duplicates = effectiveNames
            .GroupBy(static pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);

        if (duplicates is null)
        {
            return;
        }

        var fields = string.Join(", ", duplicates.Select(static pair => pair.Key));
        throw new InvalidOperationException(
            $"Result envelope field aliases must be unique. Fields [{fields}] all resolve to '{duplicates.Key}'.");
    }

    internal bool TryGetResolvedName(
        string propertyName,
        JsonSerializerOptions options,
        out string resolvedName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        ArgumentNullException.ThrowIfNull(options);

        if (Matches(propertyName, MESSAGE_PROPERTY_NAME, options))
        {
            resolvedName = GetEffectiveName(MESSAGE_PROPERTY_NAME, options);
            return true;
        }

        if (Matches(propertyName, STATUS_PROPERTY_NAME, options))
        {
            resolvedName = GetEffectiveName(STATUS_PROPERTY_NAME, options);
            return true;
        }

        if (Matches(propertyName, DATA_PROPERTY_NAME, options))
        {
            resolvedName = GetEffectiveName(DATA_PROPERTY_NAME, options);
            return true;
        }

        if (Matches(propertyName, METADATA_PROPERTY_NAME, options))
        {
            resolvedName = GetEffectiveName(METADATA_PROPERTY_NAME, options);
            return true;
        }

        resolvedName = string.Empty;
        return false;
    }

    internal string GetEffectiveName(string propertyName, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        ArgumentNullException.ThrowIfNull(options);

        return propertyName switch
        {
            MESSAGE_PROPERTY_NAME => GetConfiguredOrDefaultName(Message, MESSAGE_PROPERTY_NAME, options),
            STATUS_PROPERTY_NAME => GetConfiguredOrDefaultName(Status, STATUS_PROPERTY_NAME, options),
            DATA_PROPERTY_NAME => GetConfiguredOrDefaultName(Data, DATA_PROPERTY_NAME, options),
            METADATA_PROPERTY_NAME => GetConfiguredOrDefaultName(Metadata, METADATA_PROPERTY_NAME, options),
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unsupported result envelope property.")
        };
    }

    private static void ValidateConfiguredName(string? configuredName, string propertyName)
    {
        if (configuredName is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(configuredName))
        {
            throw new InvalidOperationException(
                $"The configured result envelope field alias for '{propertyName}' cannot be empty or whitespace.");
        }
    }

    private static bool Matches(string propertyName, string clrPropertyName, JsonSerializerOptions options)
    {
        return string.Equals(propertyName, clrPropertyName, StringComparison.Ordinal) ||
               string.Equals(propertyName, GetDefaultName(clrPropertyName, options), StringComparison.Ordinal);
    }

    private static string GetConfiguredOrDefaultName(
        string? configuredName,
        string defaultPropertyName,
        JsonSerializerOptions options)
    {
        return GetDefaultName(configuredName ?? defaultPropertyName, options);
    }

    private static string GetDefaultName(string propertyName, JsonSerializerOptions options)
    {
        return options.PropertyNamingPolicy?.ConvertName(propertyName) ?? propertyName;
    }
}
