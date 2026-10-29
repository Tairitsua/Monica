using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Monica.Repository.Snapshots.Metrics;

internal sealed class SnapshotMetrics
{
    private const string EVENT_TAG = "event";
    private readonly Counter<long> _events;
    private readonly Histogram<double> _buildDuration;
    private readonly Histogram<double> _validationDuration;

    public SnapshotMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(RepositorySnapshotMetricNames.MeterName);
        _events = meter.CreateCounter<long>(RepositorySnapshotMetricNames.Events, "events");
        _buildDuration = meter.CreateHistogram<double>(RepositorySnapshotMetricNames.BuildDuration, "ms");
        _validationDuration = meter.CreateHistogram<double>(RepositorySnapshotMetricNames.ValidationDuration, "ms");
    }

    public void Record(string name) => _events.Add(1, new KeyValuePair<string, object?>(EVENT_TAG, name));
    public void BuildCompleted(long started) => _buildDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    public void ValidationCompleted(long started) => _validationDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}
