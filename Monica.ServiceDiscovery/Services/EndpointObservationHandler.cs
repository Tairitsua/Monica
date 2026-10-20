using System.Diagnostics;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Metrics;

namespace Monica.ServiceDiscovery.Services;

internal sealed class EndpointObservationHandler(EndpointDiagnostics diagnostics) : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<string> SelectedEndpoint = new("Monica.Discovery.SelectedEndpoint");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var target = diagnostics.Find(request.RequestUri);
        if (target is null) return await base.SendAsync(request, cancellationToken);
        ((IDictionary<string, object?>)request.Options).Remove(SelectedEndpoint.Key);
        var started = Stopwatch.GetTimestamp();
        int? status = null;
        var outcome = "Failure";
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            outcome = response.IsSuccessStatusCode ? "HttpSuccess" : "HttpFailure";
            return response;
        }
        catch (InvalidOperationException exception) when (!request.Options.TryGetValue(SelectedEndpoint, out _))
        {
            throw new HttpRequestException("Service discovery could not select an endpoint.", exception);
        }
        catch (OperationCanceledException)
        {
            outcome = "Canceled";
            throw;
        }
        finally
        {
            request.Options.TryGetValue(SelectedEndpoint, out var selected);
            diagnostics.Http(target, new EndpointHttpObservation(DateTimeOffset.UtcNow, selected, status, outcome,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, Activity.Current?.TraceId.ToString()));
        }
    }
}

internal sealed class SelectedEndpointHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri)
            request.Options.Set(EndpointObservationHandler.SelectedEndpoint, EndpointDiagnostics.Authority(uri));
        return base.SendAsync(request, cancellationToken);
    }
}
