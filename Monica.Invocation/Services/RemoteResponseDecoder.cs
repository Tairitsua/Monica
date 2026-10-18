using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Modules;
using Monica.Invocation.Abstractions;
using Monica.Invocation.Exceptions;
using Monica.Invocation.Models;
using Monica.Invocation.Services.Support;

namespace Monica.Invocation.Services;

internal sealed class RemoteResponseDecoder(
    IJsonSerializerOptionsProvider serializer,
    IOptions<ModuleResultEnvelopeOption> envelopeOptions,
    IOptions<ModuleApiClientOption> apiClientOptions,
    IEnumerable<IRemoteResponseClassifier> classifiers)
{
    private readonly IReadOnlyDictionary<string, IRemoteResponseClassifier> _classifiers =
        classifiers.ToDictionary(classifier => classifier.Transport, StringComparer.Ordinal);
    private readonly JsonSerializerOptions _json = serializer.SerializerOptions;

    public async Task<TResponse> ReadAsync<TResponse>(HttpResponseMessage response, string transport, CancellationToken token)
        where TResponse : class, IRemoteResultEnvelope<TResponse>
    {
        var retryAfter = GetRetryAfter(response);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                                  mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            throw new RemoteCallException(RemoteCallFailure.FromHttp(response.StatusCode, "media_type", retryAfter));

        JsonDocument document;
        try
        {
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            if (!string.IsNullOrEmpty(charset) && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The response charset is not UTF-8.");

            await using var raw = await response.Content.ReadAsStreamAsync(token);
            await using var decoded = Decode(raw, response.Content.Headers.ContentEncoding);
            await using var bounded = new BoundedResponseStream(decoded, apiClientOptions.Value.MaxResponseBodyBytes);
            document = await JsonDocument.ParseAsync(bounded, cancellationToken: token);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or HttpRequestException)
        {
            throw new RemoteCallException(RemoteCallFailure.FromHttp(response.StatusCode, "response_body", retryAfter), exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var fields = envelopeOptions.Value.FieldNames;
                var statusName = fields.GetStatusPropertyName(_json);
                var messageName = fields.GetMessagePropertyName(_json);
                var statusMembers = FindMembers(root, statusName);
                var messageMembers = FindMembers(root, messageName);
                // Successful Res<T> values can have a null Message omitted by the host's
                // JSON ignore policy. Failure envelopes still require a message member.
                var omitsSuccessMessage = messageMembers.Count == 0 && statusMembers.Count == 1 &&
                    statusMembers[0].ValueKind == JsonValueKind.Number &&
                    statusMembers[0].TryGetInt32(out var successStatus) &&
                    successStatus is (int)ResStatus.Ok or (int)ResStatus.Created;
                if (statusMembers.Count > 0 && (messageMembers.Count > 0 || omitsSuccessMessage))
                {
                    if (statusMembers.Count != 1 || messageMembers.Count > 1 ||
                        statusMembers[0].ValueKind != JsonValueKind.Number ||
                        !statusMembers[0].TryGetInt32(out var numericStatus) ||
                        !Enum.IsDefined(typeof(ResStatus), numericStatus) || numericStatus == 0 ||
                        messageMembers.Count == 1 &&
                        messageMembers[0].ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        throw Invalid(response, "envelope_shape");

                    var status = (ResStatus)numericStatus;
                    if (new Res("", status).ToHttpStatusCode() != response.StatusCode)
                        throw Invalid(response, "status_mismatch");

                    try
                    {
                        var result = root.Deserialize<TResponse>(_json) ?? throw Invalid(response, "null_envelope");
                        if (result.Status != status) throw Invalid(response, "envelope_status");
                        if (result.Metadata is IDictionary<string, object?> metadata &&
                            metadata.ContainsKey("error") && !result.TryGetError(_json, out _))
                            throw Invalid(response, "error_contract");
                        return result;
                    }
                    catch (JsonException exception)
                    {
                        throw new RemoteCallException(RemoteCallFailure.InvalidResponse(response.StatusCode, "payload"), exception);
                    }
                }

                if (_classifiers.TryGetValue(transport, out var classifier) &&
                    classifier.TryClassify(new RemoteResponseContext(response.StatusCode, root), out var failure))
                    throw new RemoteCallException(failure with { RetryAfter = retryAfter });
            }

            throw new RemoteCallException(RemoteCallFailure.FromHttp(response.StatusCode, "non_envelope", retryAfter));
        }
    }

    private List<JsonElement> FindMembers(JsonElement root, string name) => root.EnumerateObject()
        .Where(property => string.Equals(property.Name, name,
            _json.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        .Select(property => property.Value).ToList();

    private static RemoteCallException Invalid(HttpResponseMessage response, string stage) =>
        new(RemoteCallFailure.InvalidResponse(response.StatusCode, stage));

    private static Stream Decode(Stream stream, ICollection<string> encodings)
    {
        // Validate the full stack before allocating wrappers, so an unknown inner encoding cannot leak one.
        if (encodings.Any(encoding => encoding.Trim().ToLowerInvariant() is not ("gzip" or "deflate" or "br" or "identity" or "")))
            throw new InvalidDataException("Unsupported response content encoding.");
        foreach (var encoding in encodings.Reverse())
        {
            stream = encoding.Trim().ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(stream, CompressionMode.Decompress),
                "deflate" => new DeflateStream(stream, CompressionMode.Decompress),
                "br" => new BrotliStream(stream, CompressionMode.Decompress),
                "identity" or "" => stream,
                _ => throw new InvalidDataException("Unsupported response content encoding.")
            };
        }
        return stream;
    }

    internal static string? GetRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) return null;
        var value = values.FirstOrDefault();
        return value is { Length: <= 128 } && RetryConditionHeaderValue.TryParse(value, out var parsed)
            ? parsed.ToString() : null;
    }
}
