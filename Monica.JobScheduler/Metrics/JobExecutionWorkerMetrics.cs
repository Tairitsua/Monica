using System.Diagnostics.Metrics;

namespace Monica.JobScheduler.Metrics;

internal sealed class JobExecutionWorkerMetrics
{
    private readonly Counter<long> _events;
    private readonly Histogram<double> _drainDuration;

    public JobExecutionWorkerMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(JobExecutionWorkerMetricNames.MeterName);
        _events = meter.CreateCounter<long>(JobExecutionWorkerMetricNames.RecoveryEvents, "events");
        _drainDuration = meter.CreateHistogram<double>(JobExecutionWorkerMetricNames.StoreDrainDuration, "s");
    }

    public void Record(string reason) => _events.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void Drained(string operation, TimeSpan elapsed) =>
        _drainDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("operation", operation));
}
