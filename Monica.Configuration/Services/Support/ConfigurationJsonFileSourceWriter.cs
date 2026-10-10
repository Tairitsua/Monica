using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Patches physical JSON configuration files for source-targeted mutations.
/// </summary>
internal sealed class ConfigurationJsonFileSourceWriter : IConfigurationJsonFileSourceWriter
{
    private static readonly TimeSpan FILE_LOCK_RETRY_DELAY = TimeSpan.FromMilliseconds(50);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sourceLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions WRITE_OPTIONS = CreateWriteOptions();

    private static readonly JsonDocumentOptions DOCUMENT_OPTIONS = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static JsonSerializerOptions CreateWriteOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <inheritdoc />
    public async Task<ConfigurationJsonFilePhysicalValuesSnapshot> ReadPhysicalValuesAsync(
        ConfigurationSourceDescriptor source,
        IReadOnlyList<string> configurationPaths,
        CancellationToken cancellationToken)
    {
        var read = await ReadPhysicalValuesCoreAsync(source, configurationPaths, cancellationToken);
        return new ConfigurationJsonFilePhysicalValuesSnapshot
        {
            Values = read.Values,
            Revision = read.Revision
        };
    }

    /// <inheritdoc />
    public async Task<ConfigurationJsonFileValuesSnapshot> ReadValuesAsync(
        ConfigurationSourceDescriptor source,
        ConfigurationDefinition definition,
        IReadOnlyList<string> configurationPaths,
        CancellationToken cancellationToken)
    {
        var read = await ReadPhysicalValuesCoreAsync(source, configurationPaths, cancellationToken);
        using var jsonStream = new MemoryStream(Encoding.UTF8.GetBytes(read.NormalizedText));
        var isolatedRoot = new ConfigurationBuilder().AddJsonStream(jsonStream).Build();
        using var isolatedRootLifetime = isolatedRoot as IDisposable;
        return new ConfigurationJsonFileValuesSnapshot
        {
            Values = read.Values,
            Revision = read.Revision,
            ProjectionRevision = ConfigurationProviderProjectionRevision.Compute(
                isolatedRoot.Providers.Single(),
                definition)
        };
    }

    private async Task<PhysicalValuesRead> ReadPhysicalValuesCoreAsync(
        ConfigurationSourceDescriptor source,
        IReadOnlyList<string> configurationPaths,
        CancellationToken cancellationToken)
    {
        if (source.Kind != ConfigurationSourceKind.JsonFile || string.IsNullOrWhiteSpace(source.PhysicalPath))
        {
            throw new InvalidOperationException($"Configuration source '{source.DisplayName}' is not a readable JSON file.");
        }

        var physicalPath = Path.GetFullPath(source.PhysicalPath);
        var sourceLock = _sourceLocks.GetOrAdd(physicalPath, static _ => new SemaphoreSlim(1, 1));
        await sourceLock.WaitAsync(cancellationToken);
        try
        {
            var text = File.Exists(physicalPath)
                ? await File.ReadAllTextAsync(physicalPath, cancellationToken)
                : "{}";
            var normalizedText = NormalizeJsonText(text);
            ConfigurationJsonStructureValidator.ValidateNoCaseInsensitiveDuplicates(
                normalizedText,
                DOCUMENT_OPTIONS,
                source.DisplayName);
            var root = JsonNode.Parse(normalizedText, documentOptions: DOCUMENT_OPTIONS)
                       ?? new JsonObject();
            var values = configurationPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static path => path,
                    path => Read(root, path.Split(':', StringSplitOptions.RemoveEmptyEntries)),
                    StringComparer.OrdinalIgnoreCase);
            return new PhysicalValuesRead(values, ComputeRevision(text), normalizedText);
        }
        finally
        {
            sourceLock.Release();
        }
    }

    /// <summary>
    /// Applies a source-targeted JSON mutation.
    /// </summary>
    public async Task<ConfigurationJsonFileWriteResult> WriteAsync(
        ConfigurationSourceDescriptor source,
        string configurationPath,
        ConfigurationMutationKind mutationKind,
        ConfigurationStoredValue value,
        string? expectedRevision,
        CancellationToken cancellationToken)
    {
        var batch = await WriteBatchAsync(
            source,
            [
                new ConfigurationJsonFileMutation
                {
                    ConfigurationPath = configurationPath,
                    MutationKind = mutationKind,
                    Value = value
                }
            ],
            expectedRevision,
            cancellationToken);
        var result = batch.Results[0];
        return new ConfigurationJsonFileWriteResult
        {
            OldValue = result.OldValue,
            NewValue = result.NewValue,
            OldRevision = batch.OldRevision,
            NewRevision = batch.NewRevision,
            ModifiedTime = batch.ModifiedTime
        };
    }

    /// <inheritdoc />
    public async Task<ConfigurationJsonFileBatchWriteResult> WriteBatchAsync(
        ConfigurationSourceDescriptor source,
        IReadOnlyList<ConfigurationJsonFileMutation> mutations,
        string? expectedRevision,
        CancellationToken cancellationToken)
    {
        if (mutations.Count == 0)
        {
            throw new ConfigurationValidationFailedException("At least one JSON source mutation is required.");
        }

        if (source.Kind != ConfigurationSourceKind.JsonFile || string.IsNullOrWhiteSpace(source.PhysicalPath))
        {
            throw new InvalidOperationException($"Configuration source '{source.DisplayName}' is not a writable JSON file.");
        }

        if (!source.IsWritable)
        {
            throw new InvalidOperationException(source.ReadOnlyReason ?? $"Configuration source '{source.DisplayName}' is read-only.");
        }

        var physicalPath = Path.GetFullPath(source.PhysicalPath);
        var sourceLock = _sourceLocks.GetOrAdd(physicalPath, static _ => new SemaphoreSlim(1, 1));
        await sourceLock.WaitAsync(cancellationToken);
        try
        {
            return await WriteBatchLockedAsync(
                source,
                physicalPath,
                mutations,
                expectedRevision,
                cancellationToken);
        }
        finally
        {
            sourceLock.Release();
        }
    }

    private static async Task<ConfigurationJsonFileBatchWriteResult> WriteBatchLockedAsync(
        ConfigurationSourceDescriptor source,
        string physicalPath,
        IReadOnlyList<ConfigurationJsonFileMutation> mutations,
        string? expectedRevision,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(physicalPath)!;
        Directory.CreateDirectory(directory);
        await using var fileLock = await AcquireFileLockAsync(physicalPath, cancellationToken);
        var originalText = File.Exists(physicalPath)
            ? await File.ReadAllTextAsync(physicalPath, cancellationToken)
            : "{}";
        var oldRevision = ComputeRevision(originalText);
        if (!string.IsNullOrWhiteSpace(expectedRevision)
            && !string.Equals(oldRevision, expectedRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigurationConcurrencyConflictException(
                $"Expected source revision {expectedRevision} for '{source.DisplayName}', but current revision is {oldRevision}.");
        }

        var normalizedOriginalText = NormalizeJsonText(originalText);
        ConfigurationJsonStructureValidator.ValidateNoCaseInsensitiveDuplicates(
            normalizedOriginalText,
            DOCUMENT_OPTIONS,
            source.DisplayName);
        var root = JsonNode.Parse(normalizedOriginalText, documentOptions: DOCUMENT_OPTIONS)
                   ?? new JsonObject();
        var results = new List<ConfigurationJsonFileMutationResult>(mutations.Count);
        foreach (var mutation in mutations)
        {
            var pathSegments = mutation.ConfigurationPath.Split(':', StringSplitOptions.RemoveEmptyEntries);
            var oldValue = Read(root, pathSegments);
            ApplyPhysicalMutation(root, mutation);

            results.Add(new ConfigurationJsonFileMutationResult
            {
                OldValue = oldValue,
                NewValue = mutation.MutationKind == ConfigurationMutationKind.Remove
                    ? ConfigurationStoredValue.Null
                    : Read(root, pathSegments) ?? ConfigurationStoredValue.Null
            });
        }

        var updatedText = root.ToJsonString(WRITE_OPTIONS);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(physicalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, updatedText, cancellationToken);
            var latestText = File.Exists(physicalPath)
                ? await File.ReadAllTextAsync(physicalPath, cancellationToken)
                : "{}";
            var latestRevision = ComputeRevision(latestText);
            if (!string.Equals(latestRevision, oldRevision, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigurationConcurrencyConflictException(
                    $"Configuration source '{source.DisplayName}' changed while its update was being prepared. Review the latest source before retrying.");
            }

            File.Move(temporaryPath, physicalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new ConfigurationJsonFileBatchWriteResult
        {
            Results = results,
            OldRevision = oldRevision,
            NewRevision = ComputeRevision(updatedText),
            ModifiedTime = DateTimeOffset.UtcNow
        };
    }

    private static async Task<FileStream> AcquireFileLockAsync(
        string physicalPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(physicalPath)!;
        // The lock file remains on disk so waiters always contend on the same inode across processes.
        var lockPath = Path.Combine(directory, $".{Path.GetFileName(physicalPath)}.monica.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(FILE_LOCK_RETRY_DELAY, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Gets the current source content revision.
    /// </summary>
    public async Task<string?> GetRevisionAsync(ConfigurationSourceDescriptor source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.PhysicalPath))
        {
            return null;
        }

        var physicalPath = Path.GetFullPath(source.PhysicalPath);
        var sourceLock = _sourceLocks.GetOrAdd(physicalPath, static _ => new SemaphoreSlim(1, 1));
        await sourceLock.WaitAsync(cancellationToken);
        try
        {
            var text = File.Exists(physicalPath)
                ? await File.ReadAllTextAsync(physicalPath, cancellationToken)
                : "{}";
            return ComputeRevision(text);
        }
        finally
        {
            sourceLock.Release();
        }
    }

    /// <summary>Uses the physical writer's exact path, casing, array, and removal rules without file I/O.</summary>
    internal static string PreviewMutationBatch(string documentJson,
        IReadOnlyList<ConfigurationJsonFileMutation> mutations)
    {
        ConfigurationJsonStructureValidator.ValidateNoCaseInsensitiveDuplicates(documentJson, DOCUMENT_OPTIONS, "configuration preview");
        var root = JsonNode.Parse(documentJson, documentOptions: DOCUMENT_OPTIONS) ?? new JsonObject();
        foreach (var mutation in mutations) ApplyPhysicalMutation(root, mutation);
        return root.ToJsonString();
    }

    internal static ConfigurationStoredValue? ReadSection(string documentJson, string sectionPath) =>
        Read(JsonNode.Parse(documentJson, documentOptions: DOCUMENT_OPTIONS), sectionPath.Split(':', StringSplitOptions.RemoveEmptyEntries));

    private static void ApplyPhysicalMutation(JsonNode root, ConfigurationJsonFileMutation mutation)
    {
        var segments = mutation.ConfigurationPath.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (mutation.MutationKind == ConfigurationMutationKind.Remove) Remove(root, segments);
        else
        {
            ConfigurationJsonStructureValidator.ValidateNoCaseInsensitiveDuplicates(
                mutation.Value.Json, DOCUMENT_OPTIONS, "configuration mutation");
            Set(root, segments, JsonNode.Parse(mutation.Value.Json, documentOptions: DOCUMENT_OPTIONS));
        }
    }

    private static ConfigurationStoredValue? Read(JsonNode? root, IReadOnlyList<string> segments)
    {
        var current = root;
        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            var found = current switch
            {
                JsonObject jsonObject => TryGetObjectValue(jsonObject, segment, out current),
                JsonArray jsonArray when int.TryParse(segment, out var index)
                                         && index >= 0
                                         && index < jsonArray.Count => Assign(jsonArray[index], out current),
                _ => false
            };
            if (!found)
            {
                return null;
            }

            if (current is null)
            {
                return segmentIndex == segments.Count - 1
                    ? ConfigurationStoredValue.Null
                    : null;
            }
        }

        return current is null
            ? ConfigurationStoredValue.Null
            : ConfigurationStoredValue.FromJson(current.ToJsonString());
    }

    private static void Set(JsonNode root, IReadOnlyList<string> segments, JsonNode? value)
    {
        if (segments.Count == 0)
        {
            throw new ConfigurationValidationFailedException("Source mutations require a non-root configuration path.");
        }

        var parent = GetOrCreateParent(root, segments);
        var last = segments[^1];
        switch (parent)
        {
            case JsonObject jsonObject:
                SetObjectValue(jsonObject, last, value?.DeepClone());
                break;
            case JsonArray jsonArray when int.TryParse(last, out var index):
                EnsureArraySize(jsonArray, index);
                jsonArray[index] = value?.DeepClone();
                break;
            default:
                throw new ConfigurationValidationFailedException($"Configuration path segment '{last}' cannot be written to the target JSON source.");
        }
    }

    private static void Remove(JsonNode root, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            throw new ConfigurationValidationFailedException("Source mutations require a non-root configuration path.");
        }

        var parent = GetExistingParent(root, segments);
        if (parent is null)
        {
            return;
        }

        var last = segments[^1];
        switch (parent)
        {
            case JsonObject jsonObject:
                RemoveObjectValue(jsonObject, last);
                break;
            case JsonArray jsonArray when int.TryParse(last, out var index) && index >= 0 && index < jsonArray.Count:
                jsonArray.RemoveAt(index);
                break;
        }
    }

    private static JsonNode GetOrCreateParent(JsonNode root, IReadOnlyList<string> segments)
    {
        var current = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var segment = segments[i];
            var nextIsArray = int.TryParse(segments[i + 1], out _);
            current = current switch
            {
                JsonObject jsonObject => GetOrCreateObjectChild(jsonObject, segment, nextIsArray),
                JsonArray jsonArray when int.TryParse(segment, out var index) => GetOrCreateArrayChild(jsonArray, index, nextIsArray),
                _ => throw new ConfigurationValidationFailedException($"Configuration path segment '{segment}' cannot be traversed in the target JSON source.")
            };
        }

        return current;
    }

    private static JsonNode? GetExistingParent(JsonNode root, IReadOnlyList<string> segments)
    {
        var current = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var segment = segments[i];
            current = current switch
            {
                JsonObject jsonObject => GetObjectValue(jsonObject, segment),
                JsonArray jsonArray when int.TryParse(segment, out var index) && index >= 0 && index < jsonArray.Count => jsonArray[index],
                _ => null
            };

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static JsonNode GetOrCreateObjectChild(JsonObject jsonObject, string key, bool nextIsArray)
    {
        if (GetObjectValue(jsonObject, key) is { } child)
        {
            return child;
        }

        child = nextIsArray ? new JsonArray() : new JsonObject();
        SetObjectValue(jsonObject, key, child);
        return child;
    }

    private static JsonNode? GetObjectValue(JsonObject jsonObject, string key)
    {
        return TryGetObjectValue(jsonObject, key, out var value) ? value : null;
    }

    private static bool TryGetObjectValue(JsonObject jsonObject, string key, out JsonNode? value)
    {
        if (TryGetObjectPropertyName(jsonObject, key, out var actualName))
        {
            value = jsonObject[actualName];
            return true;
        }

        value = null;
        return false;
    }

    private static bool Assign(JsonNode? candidate, out JsonNode? value)
    {
        value = candidate;
        return true;
    }

    private static void SetObjectValue(JsonObject jsonObject, string key, JsonNode? value)
    {
        jsonObject[TryGetObjectPropertyName(jsonObject, key, out var actualName) ? actualName : key] = value;
    }

    private static void RemoveObjectValue(JsonObject jsonObject, string key)
    {
        if (TryGetObjectPropertyName(jsonObject, key, out var actualName))
        {
            jsonObject.Remove(actualName);
        }
    }

    private static bool TryGetObjectPropertyName(JsonObject jsonObject, string key, out string actualName)
    {
        foreach (var property in jsonObject)
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                actualName = property.Key;
                return true;
            }
        }

        actualName = key;
        return false;
    }

    private static JsonNode GetOrCreateArrayChild(JsonArray jsonArray, int index, bool nextIsArray)
    {
        EnsureArraySize(jsonArray, index);
        if (jsonArray[index] is { } child)
        {
            return child;
        }

        child = nextIsArray ? new JsonArray() : new JsonObject();
        jsonArray[index] = child;
        return child;
    }

    private static void EnsureArraySize(JsonArray jsonArray, int index)
    {
        if (index < 0)
        {
            throw new ConfigurationValidationFailedException("Array indexes in configuration paths must not be negative.");
        }

        while (jsonArray.Count <= index)
        {
            jsonArray.Add(null);
        }
    }

    private static string ComputeRevision(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeJsonText(string content)
    {
        return string.IsNullOrWhiteSpace(content) ? "{}" : content;
    }

    private sealed record PhysicalValuesRead(
        IReadOnlyDictionary<string, ConfigurationStoredValue?> Values,
        string Revision,
        string NormalizedText);
}
