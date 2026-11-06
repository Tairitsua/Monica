using Monica.Core.Results;

namespace Monica.Configuration.UI.Support;

/// <summary>Tracks the accepted evidence from one tokenless configuration read within a borrowed lifetime.</summary>
/// <remarks>
/// A refresh retains accepted evidence until its newest result replaces or invalidates it. Successful empty
/// collections are evidence; failure and a missing successful payload are unavailable. The observation time is
/// local acceptance of that read, not a common capture time or proof of external health. The owning lifetime
/// drains actual producers; no cancellation wrapper abandons a tokenless read.
/// </remarks>
internal sealed class ConfigurationReadState<T>(ConfigurationOperationLifetime lifetime, string unavailableText,
    TimeProvider? timeProvider = null) where T : class
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _requestId;

    internal bool Loading { get; private set; }
    internal bool HasLoaded { get; private set; }
    internal T? Value { get; private set; }
    internal string? Error { get; private set; }
    internal DateTimeOffset? ObservedAtUtc { get; private set; }

    /// <summary>Reserves a new evidence generation without invoking its producer.</summary>
    internal long BeginRead(bool discardAccepted = false)
    {
        long requestId = 0;
        lifetime.TryApply(() =>
        {
            requestId = ++_requestId;
            Loading = true;
            Error = null;
            if (discardAccepted) ClearAccepted();
        });
        return requestId;
    }

    /// <summary>Owns the actual read for a previously reserved generation.</summary>
    internal Task ReadReservedAsync(long requestId, Func<Task<Result<T>>> producer, Func<T, T>? capture = null)
        => lifetime.RunAsync(_ => ReadCoreAsync(requestId, producer, capture));

    private async Task ReadCoreAsync(long requestId, Func<Task<Result<T>>> producer, Func<T, T>? capture)
    {
        if (requestId == 0) return;

        T? accepted = null;
        string? error = null;
        DateTimeOffset? observedAtUtc = null;
        try
        {
            var result = await producer();
            var failure = result;
            var value = result.Data;
            if (result.IsFailure())
                error = string.IsNullOrWhiteSpace(failure.Message) ? unavailableText : failure.Message;
            else if (value is null) error = unavailableText;
            else
            {
                accepted = capture is null ? value : capture(value);
                observedAtUtc = _timeProvider.GetUtcNow();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            error = string.IsNullOrWhiteSpace(exception.Message) ? unavailableText : exception.Message;
        }

        lifetime.TryApply(() =>
        {
            if (requestId != _requestId) return;
            Loading = false;
            Error = error;
            if (accepted is null) ClearAccepted();
            else
            {
                Value = accepted;
                HasLoaded = true;
                ObservedAtUtc = observedAtUtc;
            }
        });
    }

    private void ClearAccepted()
    {
        Value = null;
        HasLoaded = false;
        ObservedAtUtc = null;
    }
}
