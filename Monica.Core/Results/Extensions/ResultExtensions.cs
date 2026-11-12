using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Net;
using System.Text;
using Monica.Core.Results.Abstractions;

using Monica.Tool.Extensions;
// ReSharper disable once CheckNamespace
namespace Monica.Core.Results;

public static class ResultExtensions
{
    /// <summary>
    /// Get the HttpStatusCode corresponding to the response status. Every supported status maps one-to-one;
    /// values outside the <see cref="ResultStatus"/> whitelist are local contract defects.
    /// </summary>
    public static HttpStatusCode? ToHttpStatusCode(this IResultEnvelope? response)
    {
        if (response == null) return null;
        return response.Status.ToHttpStatusCode();
    }

    /// <summary>Maps a supported status to HTTP, or returns null for an unassigned status.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The status is outside the supported whitelist.</exception>
    public static HttpStatusCode? ToHttpStatusCode(this ResultStatus status)
    {
        if (status == ResultStatus.Unknown) return null;
        return Enum.IsDefined(status)
            ? (HttpStatusCode)status
            : throw new ArgumentOutOfRangeException(nameof(status), status,
                $"No HTTP status code mapping is defined for status {status}.");
    }

    /// <summary>Indicates whether the result has status 200 or 201; success does not guarantee data.</summary>
    /// <exception cref="InvalidOperationException">The status is unknown or unsupported.</exception>
    public static bool IsSuccess(this IResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status == ResultStatus.Unknown || !Enum.IsDefined(result.Status))
            throw new InvalidOperationException($"Result status {result.Status} is not a supported outcome.");
        return result.Status is ResultStatus.Ok or ResultStatus.Created;
    }

    /// <summary>Indicates whether the result has a defined failure status.</summary>
    /// <exception cref="InvalidOperationException">The status is unknown or unsupported.</exception>
    public static bool IsFailure(this IResultEnvelope result) => !result.IsSuccess();

    /// <summary>Replaces the display message on the same envelope without changing its status or payload.</summary>
    public static TSelf WithMessage<TSelf>(this TSelf result, string message) where TSelf : IResultEnvelope
    {
        ArgumentNullException.ThrowIfNull(result);
        result.Message = message;
        return result;
    }

    /// <summary>
    /// Indicates an uninitialized envelope. Remote responses require the fuller wire-contract validation performed by the RPC boundary.
    /// </summary>
    public static bool IsMalformed(this IResultEnvelope res)
    {
        return res.Status == ResultStatus.Unknown;
    }
    /// <summary>
    /// Additional information for interface settings (duplication will overwrite)
    /// </summary>
    /// <param name="res"></param>
    /// <param name="name"></param>
    /// <param name="info"></param>
    public static T SetMetadata<T>(this T res, string name, object? info = null) where T : IResultEnvelope
    {
        res.Metadata ??= new ExpandoObject();
        res.Metadata.Set(name, info);
        return res;
    }
    /// <summary>
    /// Add additional information to the interface (add a suffix if the Name is repeated)
    /// </summary>
    /// <param name="res"></param>
    /// <param name="name"></param>
    /// <param name="info"></param>
    public static T AppendMetadata<T>(this T res, string name, object? info = null) where T : IResultEnvelope
    {
        res.Metadata ??= new ExpandoObject();
        res.Metadata.Append(name, info);
        return res;
    }

    /// <summary>
    /// Adds in-process technical detail. HTTP and remote-call presentation remove this reserved member;
    /// record diagnostics through the host's logger when operators need to retain them.
    /// </summary>
    /// <param name="res">The result envelope to enrich.</param>
    /// <param name="detail">Technical detail for local consumers, never a public response contract.</param>
    public static T WithDetail<T>(this T res, object? detail) where T : IResultEnvelope
    {
        return detail is null ? res : res.SetMetadata("detail", detail);
    }

    /// <summary>
    /// Adds formatted in-process detail that is removed at HTTP and remote-call presentation boundaries.
    /// </summary>
    /// <param name="res">The result envelope to enrich.</param>
    /// <param name="format">Composite format string for technical detail.</param>
    /// <param name="args">Composite format arguments.</param>
    public static T WithDetail<T>(
        this T res,
        [StringSyntax("CompositeFormat")] string? format,
        params object?[] args) where T : IResultEnvelope
    {
        return format is null ? res : res.WithDetail((object)string.Format(format, args));
    }

    /// <summary>
    /// Batch call results are converted to single call results.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="responses"></param>
    /// <returns></returns>
    public static Result<List<T>> ToBulkResult<T>(this IEnumerable<Result<T>> responses)
    {
        var list = new List<T?>();
        var result = new Result<List<T>>()
        {
            Status = ResultStatus.Ok,
            Message = ""
        };
        var sb = new StringBuilder();
        foreach (var res in from response in responses select response)
        {
            if (res.Message is { } msg)
            {
                sb.AppendLine(msg);
            }

            if (res.Metadata is { } metadata)
            {
                result.Metadata ??= new ExpandoObject();
                result.Metadata.Append("bulk", metadata);
            }
            if (res.IsFailure() && result.Status == ResultStatus.Ok)
            {
                result.Status = res.Status;
            }
            list.Add(res.Data);
        }

        result.Message = sb.ToString().TrimEnd();
        result.Data = list.Cast<T>().ToList();
        return result;
    }
    
    /// <summary>
    /// Additional information
    /// </summary>
    /// <param name="self"></param>
    /// <param name="message"></param>
    public static T AppendMessage<T>(this T self, string? message)
        where T : IResultEnvelope
    {
        return Append(self, message);
    }
    /// <summary>
    /// Additional information
    /// </summary>
    /// <param name="self"></param>
    /// <param name="message"></param>
    private static T Append<T>(this T self, string? message)
        where T : IResultEnvelope
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            self.Message += $";{message}";
            self.Message = self.Message.TrimStart(';');
        }
        return self;
    }
    /// <summary>
    /// append error
    /// </summary>
    /// <param name="self"></param>
    /// <param name="message"></param>
    /// <param name="status"></param>
    public static T AppendFailure<T>(this T self, string? message, ResultStatus status = ResultStatus.BadRequest)
        where T : IResultEnvelope
    {
        self = Append(self, message);
        self.Status = status;
        return self;
    }
}
