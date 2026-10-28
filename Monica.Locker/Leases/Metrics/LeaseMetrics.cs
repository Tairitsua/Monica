using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace Monica.Locker.Leases.Metrics;

internal sealed class LeaseMetrics
{
    private const string OPERATION_TAG = "operation";
    private const string RESULT_TAG = "result";
    private const string REASON_TAG = "reason";
    private static readonly ConditionalWeakTable<IMeterFactory, Lazy<LeaseMetrics>> OWNERS = new();
    private readonly Counter<long> _operations;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _termsEnded;

    private LeaseMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(LeaseMetricNames.MeterName);
        _operations = meter.CreateCounter<long>(LeaseMetricNames.Operations, "operations",
            "Completed coordinator lease store calls.");
        _duration = meter.CreateHistogram<double>(LeaseMetricNames.OperationDuration, "s",
            "Coordinator lease store-call wait duration, including deadline failures.");
        _termsEnded = meter.CreateCounter<long>(LeaseMetricNames.TermsEnded, "terms",
            "Locally supervised lease terms retired, including normal shutdown.");
    }

    internal static LeaseMetrics? For(IMeterFactory? factory)
    {
        if (factory is null) return null;
        // Coordinators are explicitly constructed, independently of AddLocker. Share one owner per host factory
        // without retaining a disposed host. The factory owns its meter; coordinators never dispose it.
        // CWT callbacks can race. Only the winning lazy may publish instruments, even if publication fails.
        try { return OWNERS.GetValue(factory, static owner => new Lazy<LeaseMetrics>(() => new LeaseMetrics(owner))).Value; }
        catch (Exception) { return null; } // A throwing instrument-publication observer must not prevent coordination.
    }

    internal void RecordOperation(string operation, string result, TimeSpan elapsed)
    {
        var operationTag = new KeyValuePair<string, object?>(OPERATION_TAG, operation);
        var resultTag = new KeyValuePair<string, object?>(RESULT_TAG, result);
        // MeterListener callbacks run synchronously in the caller. Keep failures outside the ownership protocol.
        try { _operations.Add(1, operationTag, resultTag); }
        catch (Exception) { }
        try { _duration.Record(elapsed.TotalSeconds, operationTag, resultTag); }
        catch (Exception) { }
    }

    internal void RecordTermEnded(string reason)
    {
        try { _termsEnded.Add(1, new KeyValuePair<string, object?>(REASON_TAG, reason)); }
        catch (Exception) { }
    }

    internal static string FailureResult(Exception exception) => exception switch
    {
        TimeoutException => "timeout",
        OperationCanceledException => "cancelled",
        _ => "failed"
    };
}
