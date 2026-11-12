namespace Monica.Testing.Time;

/// <summary>
/// Drives worker deadlines and monotonic elapsed time without waiting for the wall clock.
/// </summary>
public sealed class ControllableTimeProvider(DateTimeOffset initialTime) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<WorkerTimer> _timers = [];
    private TaskCompletionSource _timerChanged = CreateSignal();
    private long _timestamp;
    private TimeSpan _wallOffset;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return initialTime.AddTicks(_timestamp).Add(_wallOffset);
        }
    }

    /// <summary>Adjusts only wall time, leaving elapsed time and timers unchanged.</summary>
    public void AdjustWallClock(TimeSpan offset) { lock (_gate) _wallOffset += offset; }

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new WorkerTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            ChangeTimer(timer, dueTime, period);
        }

        return timer;
    }

    /// <summary>Advances elapsed time and delivers due timers in deadline order.</summary>
    public void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        long target;
        lock (_gate)
        {
            target = checked(_timestamp + duration.Ticks);
        }

        while (true)
        {
            WorkerTimer? next;
            lock (_gate)
            {
                next = _timers
                    .Where(timer => timer.DueAt is { } dueAt && dueAt <= target)
                    .MinBy(timer => timer.DueAt);
                if (next is null)
                {
                    _timestamp = target;
                    NotifyTimerChanged();
                    return;
                }

                _timestamp = next.DueAt!.Value;
                next.DueAt = next.Period > TimeSpan.Zero
                    ? checked(_timestamp + next.Period.Ticks)
                    : null;
                NotifyTimerChanged();
            }

            next.Callback(next.State);
        }
    }

    /// <summary>
    /// Advances elapsed time before delivering due timers, simulating a process pause.
    /// </summary>
    public void AdvanceAfterPause(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        WorkerTimer[] due;
        lock (_gate)
        {
            _timestamp = checked(_timestamp + duration.Ticks);
            due = _timers.Where(timer => timer.DueAt is { } dueAt && dueAt <= _timestamp).ToArray();
            foreach (var timer in due)
            {
                timer.DueAt = timer.Period > TimeSpan.Zero
                    ? checked(_timestamp + timer.Period.Ticks)
                    : null;
            }

            NotifyTimerChanged();
        }

        foreach (var timer in due)
        {
            timer.Callback(timer.State);
        }
    }

    /// <summary>
    /// Waits until an active timer targets the specified elapsed time since this provider was created.
    /// </summary>
    public async Task WaitForTimerAsync(TimeSpan elapsedDeadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_timers.Any(timer => timer.DueAt == elapsedDeadline.Ticks))
                {
                    return;
                }

                changed = _timerChanged.Task;
            }

            await changed.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private bool ChangeTimer(WorkerTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        if (timer.IsDisposed)
        {
            return false;
        }

        timer.DueAt = dueTime == Timeout.InfiniteTimeSpan
            ? null
            : checked(_timestamp + dueTime.Ticks);
        timer.Period = period;
        NotifyTimerChanged();
        return true;
    }

    private void NotifyTimerChanged()
    {
        var changed = _timerChanged;
        _timerChanged = CreateSignal();
        changed.TrySetResult();
    }

    private static TaskCompletionSource CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class WorkerTimer(ControllableTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public long? DueAt { get; set; }

        public TimeSpan Period { get; set; }

        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                return owner.ChangeTimer(this, dueTime, period);
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                IsDisposed = true;
                DueAt = null;
                owner._timers.Remove(this);
                owner.NotifyTimerChanged();
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
