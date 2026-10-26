using System.Diagnostics.Metrics;

namespace Monica.JobScheduler.EfCore.Metrics;

internal sealed class JobSchedulerStoreMetrics
{
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _operations;
    private readonly Counter<long> _retries;

    public JobSchedulerStoreMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(JobSchedulerStoreMetricNames.MeterName);
        _duration = meter.CreateHistogram<double>(JobSchedulerStoreMetricNames.OperationDuration, "s");
        _operations = meter.CreateCounter<long>(JobSchedulerStoreMetricNames.Operations, "operations");
        _retries = meter.CreateCounter<long>(JobSchedulerStoreMetricNames.TransactionRetries, "retries");
    }

    public void Record(string operation, string outcome, TimeSpan elapsed)
    {
        var operationTag = new KeyValuePair<string, object?>("operation", operation);
        var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);
        _duration.Record(elapsed.TotalSeconds, operationTag, outcomeTag);
        _operations.Add(1, operationTag, outcomeTag);
    }

    public void Retry() => _retries.Add(1);
}
