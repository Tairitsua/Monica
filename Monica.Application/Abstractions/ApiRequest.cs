using Monica.Application.Abstractions;

namespace Monica.Invocation.Models;

/// <summary>A result request with optional HTTP caller headers. New transport-neutral contracts can implement IResultRequest directly.</summary>
public record ApiRequest<TResponse> : ApiRequest, IResultRequest<TResponse>;
