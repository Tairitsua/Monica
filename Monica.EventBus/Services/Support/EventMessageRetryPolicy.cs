using Microsoft.Extensions.Options;
using Monica.Modules;

namespace Monica.EventBus.Services.Support;

/// <summary>
/// Bounds processing attempts for one distributed transport delivery, independently of exception type.
/// </summary>
/// <remarks>
/// The callback must enter a fresh receive scope on each attempt. Transport acknowledgment and dead-letter
/// forwarding belong to the provider after this policy completes. The budget is local to this invocation;
/// broker redelivery after a restart or failed acknowledgment starts a new budget.
/// </remarks>
public sealed class EventMessageRetryPolicy(IOptions<ModuleEventBusOption> options)
{
    private readonly int _retryCount = ValidateRetryCount(options.Value.MessageRetryCount);

    private static int ValidateRetryCount(int retryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryCount, nameof(ModuleEventBusOption.MessageRetryCount));
        return retryCount;
    }

    /// <summary>
    /// Executes the callback once, then retries any failure up to the configured count. Throws the final
    /// failure when exhausted. Cancellation of <paramref name="cancellationToken"/> stops processing
    /// without retrying; a callback's independent cancellation consumes the ordinary retry budget.
    /// </summary>
    public async Task ExecuteAsync(Func<Task> handle, CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await handle().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && retry < _retryCount)
            {
                // Each invocation re-enters the receive pipeline after the attempt callback exits.
            }
        }
    }
}
