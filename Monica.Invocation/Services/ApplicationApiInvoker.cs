using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Monica.ApplicationModel.Abstractions;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Mediator;
using Monica.Core.Results.Abstractions;
using Monica.Modules;
using Monica.Invocation.Abstractions;
using Monica.Invocation.Extensions;
using Monica.Invocation.Models;

namespace Monica.Invocation.Services;

internal sealed class ApplicationApiInvoker(
    IApplicationModel model,
    ILocalApiInvoker local,
    IRemoteCallClient remote,
    IHttpClientFactory clients,
    ApplicationApiClientCatalog catalog,
    IJsonSerializerOptionsProvider serializer,
    IOptions<ModuleApiClientOption> apiClientOptions,
    IEnumerable<IApplicationInvocationAuthorityPolicy> authorities,
    IServiceProvider services) : IApplicationApiInvoker
{
    private readonly IApplicationInvocationAuthorityPolicy? _authority = authorities.SingleOrDefault();

    public Task<TResponse> InvokeAsync<TResponse>(IRequest<TResponse> request, ApiHttpEndpoint endpoint,
        CancellationToken cancellationToken = default, ApiTransaction transaction = ApiTransaction.Shared)
        where TResponse : class, IRemoteResultEnvelope<TResponse>
        => ApiTransactionPolicy.ExecuteAsync(services, transaction,
            () => InvokeCoreAsync(request, endpoint, cancellationToken, transaction), isReadOnly: request is IQueryRequest,
            operation: request?.GetType().FullName);

    private async Task<TResponse> InvokeCoreAsync<TResponse>(IRequest<TResponse> request, ApiHttpEndpoint endpoint,
        CancellationToken cancellationToken, ApiTransaction transaction) where TResponse : class, IRemoteResultEnvelope<TResponse>
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        var requestType = request.GetType();
        var operation = model.GetOperation(requestType);
        model.LocalHandlers.TryGetValue(requestType, out var localHandler);
        var destination = _authority is null ? null : await _authority.ResolveAsync(
            new ApplicationInvocationAuthorityContext(request, operation, localHandler), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (destination is null)
        {
            if (localHandler is not null && (operation.RequiredRole is null || model.IsRoleActive(operation.RequiredRole)))
                return await local.InvokeAsync(request, cancellationToken, transaction);
            destination = new ApplicationInvocationDestination(model.ResolveRoleAppId(operation.RequiredRole ?? operation.RoleId));
        }
        else if (operation.RequiredRole is { } required && model.ResolveRoleAppId(required) != destination.AppId)
        {
            throw new InvalidOperationException($"Authority destination '{destination.AppId}' conflicts with the owner of required role '{required}'.");
        }

        ApiTransactionPolicy.EnsureRemoteAllowed(services, transaction);
        using var client = clients.CreateClient(catalog.GetClientName(destination.AppId, destination.ProviderKey));
        var operationName = requestType.Name.StartsWith("Command", StringComparison.Ordinal) ? requestType.Name["Command".Length..]
            : requestType.Name.StartsWith("Query", StringComparison.Ordinal) ? requestType.Name["Query".Length..] : requestType.Name;
        return await remote.InvokeAsync<TResponse>(client, token =>
        {
            token.ThrowIfCancellationRequested();
            var message = new HttpRequestMessage(new HttpMethod(endpoint.Method),
                request.BuildApiRequestUri(endpoint.RouteTemplate, endpoint.IncludeQueryString, serializer.DateTimeFormat));
            try
            {
                if (endpoint.IncludeBody)
                    message.Content = JsonContent.Create(request, requestType, options: serializer.SerializerOptions);
                return ValueTask.FromResult(message);
            }
            catch
            {
                message.Dispose();
                throw;
            }
        }, new RemoteCallContext(new ApiServiceDescriptor(operation.DomainId, destination.AppId), operationName,
            endpoint.RouteTemplate, apiClientOptions.Value.ProviderTransport), cancellationToken);
    }
}
