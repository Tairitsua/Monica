using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Test.Monica.Repository.Outbox;

/// <summary>Separates elapsed-time advancement from explicitly released worker delays.</summary>
internal sealed class OutboxTestTimeProvider : TimeProvider
{
    private readonly ConcurrentDictionary<TimeSpan, Channel<Delay>> _delays = new();
    private long _elapsedTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _elapsedTicks);
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2031, 1, 15, 0, 0, 0, TimeSpan.Zero)
        .AddTicks(GetTimestamp());

    public void Advance(TimeSpan amount) => Interlocked.Add(ref _elapsedTicks, amount.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot test delays are supported.");
        var delay = new Delay(callback, state);
        _delays.GetOrAdd(dueTime, _ => Channel.CreateUnbounded<Delay>()).Writer.TryWrite(delay);
        return delay;
    }

    public async Task<Delay> TakeDelayAsync(TimeSpan interval, CancellationToken cancellationToken)
        => await _delays.GetOrAdd(interval, _ => Channel.CreateUnbounded<Delay>()).Reader
            .ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);

    internal sealed class Delay(TimerCallback callback, object? state) : ITimer
    {
        private int _active = 1;

        public void Fire()
        {
            if (Interlocked.Exchange(ref _active, 0) == 1) callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
            => throw new NotSupportedException("Test delays cannot be rescheduled.");

        public void Dispose() => Interlocked.Exchange(ref _active, 0);
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
