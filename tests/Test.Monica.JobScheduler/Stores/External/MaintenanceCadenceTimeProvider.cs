using System.Threading.Channels;

namespace Test.Monica.JobScheduler.Stores.External;

// Controls worker cadence only. Authority clocks, elapsed time and lease timers use real time during native measurements.
internal sealed class MaintenanceCadenceTimeProvider : TimeProvider
{
    private readonly Channel<Ticket> _tickets = Channel.CreateUnbounded<Ticket>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period == Timeout.InfiniteTimeSpan && dueTime.TotalSeconds is 3 or 60 or 61)
        {
            var ticket = new Ticket(callback, state, dueTime);
            _tickets.Writer.TryWrite(ticket);
            return ticket;
        }
        return System.CreateTimer(callback, state, dueTime, period);
    }

    internal async Task<Ticket[]> TakeAsync(int count, CancellationToken cancellationToken)
    {
        var result = new Ticket[count];
        for (var index = 0; index < count; index++)
            result[index] = await _tickets.Reader.ReadAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        return result;
    }

    internal sealed class Ticket(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        private int _finished;
        internal TimeSpan Due { get; } = due;
        internal void Fire()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0) callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException("Worker cadence tickets are one-shot.");
        public void Dispose() => Interlocked.Exchange(ref _finished, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
