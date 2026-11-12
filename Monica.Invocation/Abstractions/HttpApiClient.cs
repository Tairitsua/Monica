using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.Invocation.Extensions;
using Monica.Core.Results.Abstractions;
using Monica.Invocation.Models;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.Invocation.Services;

namespace Monica.Invocation.Abstractions;

/// <summary>
/// Provides the shared HTTP transport behavior for generated API clients.
/// </summary>
/// <param name="serviceProvider">The cached service provider owned by the current Monica host.</param>
/// <param name="httpClient">The HTTP client configured for the remote API domain.</param>
public abstract class HttpApiClient(ICachedServiceProvider serviceProvider, HttpClient httpClient) : ApiClient(serviceProvider)
{
    private readonly IJsonSerializerOptionsProvider _serializerOptionsProvider = serviceProvider
        .GetRequiredService<IJsonSerializerOptionsProvider>();

    protected readonly HttpClient HttpClient = httpClient;

    /// <summary>Constructs an owned request, disposing partially created content on a local factory failure.</summary>
    protected HttpRequestMessage CreateHttpRequest<TRequest>(TRequest request, HttpMethod method,
        string routeTemplate, bool includeQueryString, bool includeBody)
    {
        var message = new HttpRequestMessage(method, CreateRequestUri(request, routeTemplate, includeQueryString));
        try
        {
            if (includeBody) message.Content = CreateJsonRequestContent(request);
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    /// <summary>Executes a generated request through the host-owned remote boundary.</summary>
    /// <remarks>The request factory transfers ownership when it returns. Caller cancellation propagates.</remarks>
    protected Task<TResponse> ExecuteAsync<TResponse>(
        Func<CancellationToken, ValueTask<HttpRequestMessage>> createRequest,
        RemoteCallContext context, CancellationToken cancellationToken)
        where TResponse : class, IRemoteResultEnvelope<TResponse>
    {
        var options = CachedServiceProvider.GetRequiredService<IOptions<ModuleApiClientOption>>().Value;
        return ApiTransactionPolicy.ExecuteAsync(CachedServiceProvider, Transaction,
            () => CachedServiceProvider.GetRequiredService<IRemoteCallClient>().InvokeAsync<TResponse>(
                HttpClient, createRequest, context with { Transport = options.ProviderTransport }, cancellationToken),
            remote: true, operation: context.Operation);
    }

    /// <summary>
    /// Creates a relative request URI using the date-time wire policy owned by the current Monica host.
    /// </summary>
    /// <typeparam name="TRequest">The request contract type.</typeparam>
    /// <param name="request">The request whose route and query properties should be serialized.</param>
    /// <param name="routeTemplate">The complete route template.</param>
    /// <param name="includeQueryString">Whether non-route properties should be appended to the query string.</param>
    /// <returns>The escaped relative request URI.</returns>
    protected string CreateRequestUri<TRequest>(
        TRequest request,
        string routeTemplate,
        bool includeQueryString)
    {
        return request.BuildApiRequestUri(
            routeTemplate,
            includeQueryString,
            _serializerOptionsProvider.DateTimeFormat);
    }

    /// <summary>
    /// Creates JSON request content using the serializer options owned by the current Monica host.
    /// </summary>
    /// <typeparam name="TRequest">The request contract type.</typeparam>
    /// <param name="request">The request to serialize.</param>
    /// <returns>HTTP content containing the serialized request.</returns>
    /// <remarks>
    /// Override this method when a transport requires a different JSON media type or content implementation.
    /// </remarks>
    protected virtual HttpContent CreateJsonRequestContent<TRequest>(TRequest request)
    {
        return JsonContent.Create(request, options: _serializerOptionsProvider.SerializerOptions);
    }
}
