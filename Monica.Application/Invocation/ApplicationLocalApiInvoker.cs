using Microsoft.Extensions.DependencyInjection;
using Monica.Application.Abstractions;
using Monica.Authority.Identity.Abstractions;
using Monica.Authority.Identity.Services;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.Execution;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Mediator;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Invocation.Abstractions;
using Monica.Invocation.Models;

namespace Monica.Application.Invocation;

internal sealed class ApplicationLocalApiInvoker(
    IServiceScopeFactory scopes,
    ExecutionScopeContext operation,
    IApplicationCallerContext caller,
    CurrentPrincipalContext principalContext,
    IEnumerable<IApplicationOperationContributor> contributors) : ILocalApiInvoker
{
    public async Task<TResult> InvokeAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default,
        ApiTransaction transaction = ApiTransaction.Shared)
        where TResult : class, IRemoteResultEnvelope<TResult>
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(transaction)) throw new ArgumentOutOfRangeException(nameof(transaction));
        cancellationToken.ThrowIfCancellationRequested();
        if (transaction == ApiTransaction.Shared && operation.Services is { } activeServices)
        {
            // Nested failures must unwind the existing pipeline before any boundary maps them to results.
            return await activeServices.GetRequiredService<IMediator>().Send(request, cancellationToken);
        }

        var principal = operation.Services is { } parent
            ? parent.GetRequiredService<ICurrentPrincipalAccessor>().Principal
            : await caller.GetPrincipalAsync(cancellationToken);
        using var identity = principalContext.Use(principal);
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var operationLease = operation.Enter(services);
        TResult result;
        try
        {
            foreach (var contributor in contributors)
                await contributor.InitializeAsync(services, cancellationToken);
            result = await services.GetRequiredService<IMediator>().Send(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = await services.GetRequiredService<IExceptionHandlerService>()
                .HandleAsync(null, exception, cancellationToken);
            result = TResult.CreateRemoteFailure(failure.Status, failure.Message ?? string.Empty);
            result.Metadata = failure.Metadata;
        }

        return result.PrepareForPresentation(services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions,
            services.GetRequiredService<IResultErrorMessageProvider>(), ResultTraceId.Capture(null));
    }
}
