using System.Diagnostics.Metrics;

namespace Monica.Invocation.Metrics;

/// <summary>Meter identity for host-owned exporters of complete remote invocation measurements.</summary>
public static class InvocationMetricNames
{
    /// <summary>Meter emitted by the Invocation module.</summary>
    public const string MeterName = "Monica.Invocation";
}

internal sealed class InvocationMetrics(IMeterFactory meters)
{
    private readonly Histogram<double> _duration = meters.Create(InvocationMetricNames.MeterName)
        .CreateHistogram<double>("monica.invocation.remote.duration", "s",
            "Complete remote call duration, including response decoding.");

    public void Record(double seconds, string transport, string outcome) => _duration.Record(seconds,
        new("transport", transport is "http" or "dapr" or "grpc" ? transport : "other"), new("outcome", outcome));
}
