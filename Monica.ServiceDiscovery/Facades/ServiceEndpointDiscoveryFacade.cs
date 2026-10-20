using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.ServiceDiscovery;
using Monica.Modules;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Metrics;
using Monica.Core.Results;

namespace Monica.ServiceDiscovery.Facades;

/// <summary>
/// Captures process-local discovery evidence for UI/administration. Refresh resolves only declared targets
/// through the host's native resolver; it sends no business or health requests and does not force cache expiry.
/// </summary>
public sealed class ServiceEndpointDiscoveryFacade
{
    private readonly ServiceEndpointResolver _resolver;
    private readonly EndpointDiagnostics _diagnostics;
    private readonly ModuleServiceEndpointDiscoveryOption _options;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    internal ServiceEndpointDiscoveryFacade(ServiceEndpointResolver resolver, EndpointDiagnostics diagnostics,
        IOptions<ModuleServiceEndpointDiscoveryOption> options)
    {
        _resolver = resolver;
        _diagnostics = diagnostics;
        _options = options.Value;
    }

    /// <summary>Returns detached retained evidence without triggering network activity.</summary>
    public Res<ServiceEndpointDiagnosticsSnapshot> Capture() => Res.Ok(_diagnostics.Capture());

    /// <summary>
    /// Resolves declared targets with per-target deadlines and a 30-second overall budget. Cancellation propagates.
    /// A concurrent refresh is not duplicated. Failure retains prior candidates with their original timestamp.
    /// </summary>
    public async Task<Res> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refresh.WaitAsync(0, cancellationToken)) return Res.Ok();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            foreach (var target in _options.Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget.IsCancellationRequested) break;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                deadline.CancelAfter(_options.ResolutionTimeout);
                var start = Stopwatch.GetTimestamp();
                try
                {
                    var source = await _resolver.GetEndpointsAsync(target.Address.AbsoluteUri, deadline.Token);
                    var endpoints = source.Endpoints.Take(128).Select(e => new DiscoveredEndpoint(
                        EndpointDiagnostics.Address(e.EndPoint),
                        e.Features.Get<IServiceEndpointProvider>()?.GetType().Name ?? "Unknown")).ToArray();
                    _diagnostics.Resolution(target, endpoints.Length == 0 ? "Empty" : "Resolved", endpoints,
                        Stopwatch.GetElapsedTime(start).TotalSeconds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    _diagnostics.Resolution(target, exception is OperationCanceledException ? "Timeout" : "Failed",
                        null, Stopwatch.GetElapsedTime(start).TotalSeconds);
                }
            }
        }
        finally { _refresh.Release(); }
        return Res.Ok();
    }
}
