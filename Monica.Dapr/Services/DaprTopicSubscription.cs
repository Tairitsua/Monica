using Dapr.Messaging.PublishSubscribe;
using Microsoft.Extensions.Logging;

using Monica.Dapr.Abstractions;
using Monica.Modules;

namespace Monica.Dapr.Services;

/// <summary>
/// Owns the complete lifecycle of one Dapr streaming topic subscription.
/// </summary>
/// <remarks>
/// The Dapr SDK reports background stream failures but does not reconnect automatically. This owner serializes
/// connection generations so a failed stream is cancelled and disposed before its replacement is created.
/// </remarks>
internal sealed class DaprTopicSubscription(
    DaprPublishSubscribeClient daprClient,
    IDaprSidecarHealthCoordinator healthCoordinator,
    ModuleDaprEventBusOption options,
    DaprSubscriptionRecoveryPolicy recoveryPolicy,
    string topicName,
    string? deadLetterTopic,
    TopicMessageHandler messageHandler,
    CancellationToken applicationStopping,
    Action<string> onReceiverCreated,
    Action<string> onStable,
    Action<string, Exception, TimeSpan> onRecoveryScheduled,
    Action<string, Exception> onCleanupFailed,
    Action<string, Exception> onSupervisorFailed,
    ILogger<DaprTopicSubscription> logger) : IAsyncDisposable
{
    private readonly object _lifecycleLock = new();
    private readonly CancellationTokenSource _lifetime =
        CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
    private TaskCompletionSource<Exception>? _activeFaultSignal;
    private Task? _executionTask;
    private Task? _disposeTask;

    /// <summary>
    /// Starts the subscription supervisor. Calling this method more than once has no effect.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);

            _executionTask ??= RunAsync(_lifetime.Token);
        }
    }

    /// <summary>
    /// Requests replacement of the current stream, for example after the sidecar recovers from an unhealthy state.
    /// </summary>
    public void RequestReconnect(Exception reason)
    {
        Volatile.Read(ref _activeFaultSignal)?.TrySetResult(reason);
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SuperviseAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cancellation is the normal terminal state for a subscription supervisor.
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                exception,
                "Dapr subscription supervisor terminated unexpectedly for topic {Topic}",
                topicName);
            NotifySafely(
                () => onSupervisorFailed(topicName, exception),
                "terminal supervisor failure");
        }
    }

    private async Task SuperviseAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            await WaitForHealthySidecarAsync(stoppingToken);

            var faultSignal = new TaskCompletionSource<Exception>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _activeFaultSignal, faultSignal);

            IAsyncDisposable? subscription = null;
            Exception failure;
            using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            try
            {
                var subscriptionOptions = new DaprSubscriptionOptions(
                    // Monica applies a fresh deadline to each bounded processing attempt. The SDK
                    // token is reserved for stream shutdown so retries never inherit an expired deadline.
                    new MessageHandlingPolicy(Timeout.InfiniteTimeSpan, TopicResponseAction.Retry))
                {
                    DeadLetterTopic = deadLetterTopic,
                    MaximumCleanupTimeout = options.MaximumCleanupTimeout,
                    MaximumQueuedMessages = options.MaximumQueuedMessages,
                    ErrorHandler = exception =>
                    {
                        faultSignal.TrySetResult(exception);
                        return Task.CompletedTask;
                    }
                };

                subscription = await daprClient.SubscribeAsync(
                    options.PubSubName,
                    topicName,
                    subscriptionOptions,
                    messageHandler,
                    connectionLifetime.Token);

                NotifySafely(() => onReceiverCreated(topicName), "receiver creation");

                var stabilityTask = Task.Delay(recoveryPolicy.StabilityPeriod, connectionLifetime.Token);
                var firstSignal = await Task.WhenAny(faultSignal.Task, stabilityTask);
                if (firstSignal == stabilityTask)
                {
                    await stabilityTask;
                    consecutiveFailures = 0;
                    NotifySafely(() => onStable(topicName), "stable receiver");
                }

                failure = await faultSignal.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Interlocked.CompareExchange(ref _activeFaultSignal, null, faultSignal);
                await CancelConnectionAsync(connectionLifetime);

                if (subscription is not null)
                {
                    await DisposeSubscriptionAsync(subscription);
                }
            }

            stoppingToken.ThrowIfCancellationRequested();
            consecutiveFailures++;
            var delay = recoveryPolicy.CalculateDelay(consecutiveFailures, topicName);
            NotifySafely(
                () => onRecoveryScheduled(topicName, failure, delay),
                "recovery scheduling");
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task WaitForHealthySidecarAsync(CancellationToken cancellationToken)
    {
        while (!healthCoordinator.IsHealthy)
        {
            await Task.Delay(recoveryPolicy.InitialDelay, cancellationToken);
        }
    }

    private async Task DisposeSubscriptionAsync(IAsyncDisposable subscription)
    {
        try
        {
            await subscription.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected while replacing a failed stream or stopping the host.
        }
        catch (Exception exception)
        {
            NotifySafely(
                () => onCleanupFailed(topicName, exception),
                "receiver cleanup failure");
        }
    }

    private async Task CancelConnectionAsync(CancellationTokenSource connectionLifetime)
    {
        try
        {
            await connectionLifetime.CancelAsync();
        }
        catch (Exception exception)
        {
            NotifySafely(
                () => onCleanupFailed(topicName, exception),
                "receiver cancellation failure");
        }
    }

    private void NotifySafely(Action notification, string notificationKind)
    {
        try
        {
            notification();
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Dapr subscription {NotificationKind} notification failed for topic {Topic}",
                notificationKind,
                topicName);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            _disposeTask ??= DisposeCoreAsync(_executionTask);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task? executionTask)
    {
        try
        {
            try
            {
                await _lifetime.CancelAsync();
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to signal Dapr subscription supervisor shutdown for topic {Topic}",
                    topicName);
                NotifySafely(
                    () => onCleanupFailed(topicName, exception),
                    "supervisor shutdown failure");
            }

            if (executionTask is not null)
            {
                await executionTask;
            }
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
}
