using System.Diagnostics;
using System.Dynamic;
using System.Text.Json.Serialization;
using Monica.Core.Results.Abstractions;
using Monica.Tool.Diagnostics;
using Monica.Tool.Extensions;

namespace Monica.Core.Results;

/// <summary>A mutable result envelope without a payload.</summary>
/// <remarks>The parameterless constructor leaves the status unknown for serializers and adapters.</remarks>
[DebuggerDisplay("{GetDebugValue()}")]
public class Result : IRemoteResultEnvelope<Result>
{
    /// <inheritdoc />
    public string? Message { get; set; } = "";

    /// <inheritdoc />
    public ResultStatus Status { get; set; } = ResultStatus.Unknown;

    /// <inheritdoc />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExpandoObject? Metadata { get; set; }

    /// <summary>Creates an envelope with an unknown status for serializers and adapters.</summary>
    public Result() { }

    /// <summary>Creates a successful result with no payload and an empty message.</summary>
    public static Result Success() => new() { Status = ResultStatus.Ok };

    /// <summary>Creates a successful result with the specified payload and a null message.</summary>
    /// <remarks>A successful result may have null data; use a nullable payload type when appropriate.</remarks>
    public static Result<T> Success<T>(T data) => new() { Status = ResultStatus.Ok, Message = null, Data = data };

    /// <summary>Creates a result with status 201 and no payload. This does not set a Location header.</summary>
    public static Result Created() => new() { Status = ResultStatus.Created };

    /// <summary>Creates a result with status 201 and the specified payload. This does not set a Location header.</summary>
    public static Result<T> Created<T>(T data) => new() { Status = ResultStatus.Created, Message = null, Data = data };

    /// <summary>Creates a failure with a safe display message and a supported failure status.</summary>
    /// <param name="message">A safe message suitable for display to the caller.</param>
    /// <param name="status">The failure status, defaulting to <see cref="ResultStatus.BadRequest"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The status is not a defined failure status.</exception>
    public static Result Failure(string message, ResultStatus status = ResultStatus.BadRequest)
    {
        ValidateFailureStatus(status);
        return new Result { Message = message, Status = status };
    }

    /// <summary>Creates a typed failure with a safe display message and a supported failure status.</summary>
    /// <param name="message">A safe message suitable for display to the caller.</param>
    /// <param name="status">The failure status, defaulting to <see cref="ResultStatus.BadRequest"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The status is not a defined failure status.</exception>
    public static Result<T> Failure<T>(string message, ResultStatus status = ResultStatus.BadRequest)
    {
        ValidateFailureStatus(status);
        return new Result<T> { Message = message, Status = status };
    }

    /// <summary>Copies a failure without its payload, preserving the message, status, and metadata.</summary>
    /// <remarks>The metadata dictionary is copied; its values are shared.</remarks>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="InvalidOperationException">The source is successful or has an invalid status.</exception>
    public static Result FromFailure(IResultEnvelope source)
    {
        ValidateFailureSource(source);
        return new Result { Message = source.Message, Status = source.Status, Metadata = CopyMetadata(source.Metadata) };
    }

    /// <summary>Copies a failure to a new payload type without carrying the source payload.</summary>
    /// <remarks>The metadata dictionary is copied; its values are shared.</remarks>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="InvalidOperationException">The source is successful or has an invalid status.</exception>
    public static Result<T> FromFailure<T>(IResultEnvelope source)
    {
        ValidateFailureSource(source);
        return new Result<T> { Message = source.Message, Status = source.Status, Metadata = CopyMetadata(source.Metadata) };
    }

    /// <inheritdoc />
    public static Result CreateRemoteFailure(ResultStatus status, string message) => Failure(message, status);

    /// <summary>Creates a typed envelope with the supplied data while preserving status, message, and metadata.</summary>
    /// <remarks>This operation does not change the classification. The metadata dictionary is copied; its values are shared.</remarks>
    public Result<T> WithData<T>(T data) => new()
    {
        Message = Message,
        Status = Status,
        Metadata = CopyMetadata(Metadata),
        Data = data
    };

    /// <inheritdoc />
    public override string ToString() => Message ?? "";

    internal string GetDebugValue() => Metadata is not null
        ? $"{Message}({Status})\n{Metadata.ToJsonString()!}"
        : $"{Message}({Status})";

    internal static void ValidateFailureStatus(ResultStatus status)
    {
        if (status is ResultStatus.Unknown or ResultStatus.Ok or ResultStatus.Created || !Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "A defined failure status is required.");
    }

    internal static void ValidateFailureSource(IResultEnvelope source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsFailure())
            throw new InvalidOperationException("A failure envelope is required.");
    }

    internal static ExpandoObject? CopyMetadata(ExpandoObject? source)
    {
        if (source is null) return null;
        var copy = new ExpandoObject();
        var entries = (IDictionary<string, object?>)copy;
        foreach (var entry in source) entries.Add(entry);
        return copy;
    }
}

/// <summary>A mutable result envelope with a declared payload type.</summary>
/// <typeparam name="T">The payload type. A failure may also carry meaningful data.</typeparam>
/// <remarks>Success does not guarantee non-null data. The serializer constructor leaves status unknown.</remarks>
[DebuggerDisplay("{GetDebugValue()}")]
public class Result<T> : IRemoteResultEnvelope<Result<T>>
{
    /// <inheritdoc />
    public string? Message { get; set; } = "";

    /// <inheritdoc />
    public ResultStatus Status { get; set; } = ResultStatus.Unknown;

    /// <inheritdoc />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExpandoObject? Metadata { get; set; }

    /// <summary>Gets or sets the payload; it can be present on failure or absent on success.</summary>
    /// <remarks>For a non-nullable value type, an absent payload is its default value; use a nullable type when needed.</remarks>
    public T? Data { get; set; }

    /// <summary>Creates an envelope with an unknown status for serializers and adapters.</summary>
    public Result() { }

    /// <inheritdoc />
    public static Result<T> CreateRemoteFailure(ResultStatus status, string message) => Result.Failure<T>(message, status);

    /// <summary>Creates an untyped envelope, intentionally discarding the payload.</summary>
    /// <remarks>Status and message are preserved; the metadata dictionary is copied and its values are shared.</remarks>
    public Result WithoutData() => new()
    {
        Message = Message,
        Status = Status,
        Metadata = Result.CopyMetadata(Metadata)
    };

    internal string GetDebugValue() => Metadata is not null
        ? $"{Message}({Status}) Data: {Data?.ToJsonStringForce()?.LimitMaxLength(500, "...")}\n{Metadata.ToJsonString()?.LimitMaxLength(500, "...")}"
        : $"{Message}({Status}) Data: {Data?.ToJsonStringForce()?.LimitMaxLength(500, "...")}";
}
