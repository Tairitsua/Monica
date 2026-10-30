using Microsoft.Extensions.Options;
using Monica.Core.ExceptionHandling.Exceptions;
using Monica.EventBus.Services.Support;
using Monica.Modules;
using Xunit;

namespace Test.Monica.EventBus.Services;

public sealed class EventMessageRetryPolicyTests
{
    public static TheoryData<Exception> Failures => new()
    {
        new BusinessException("Rejected."),
        new InvalidOperationException("Unavailable."),
        new TimeoutException("Timed out."),
        new OperationCanceledException("Handler canceled independently.")
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task ExecuteAsync_WhenProcessingFails_ShouldRetryOnceByDefaultAndPreserveFinalFailure(Exception failure)
    {
        var policy = new EventMessageRetryPolicy(Options.Create(new ModuleEventBusOption()));
        var attempts = 0;
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => policy.ExecuteAsync(() =>
        {
            attempts++;
            throw failure;
        }, TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    public async Task ExecuteAsync_WhenBudgetIsConfigured_ShouldStopAtTheExactAttemptCount(int retries, int expectedAttempts)
    {
        var policy = new EventMessageRetryPolicy(Options.Create(new ModuleEventBusOption { MessageRetryCount = retries }));
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromException(new InvalidOperationException("Still unavailable."));
        }, TestContext.Current.CancellationToken));

        Assert.Equal(expectedAttempts, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRetrySucceeds_ShouldStopAndGiveTheNextDeliveryItsOwnBudget()
    {
        var policy = new EventMessageRetryPolicy(Options.Create(new ModuleEventBusOption { MessageRetryCount = 3 }));
        for (var delivery = 0; delivery < 2; delivery++)
        {
            var attempts = 0;
            await policy.ExecuteAsync(() => ++attempts == 1
                ? Task.FromException(new InvalidOperationException("Temporary failure."))
                : Task.CompletedTask, TestContext.Current.CancellationToken);
            Assert.Equal(2, attempts);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenTransportStops_ShouldNotStartAnotherAttempt()
    {
        using var stopping = new CancellationTokenSource();
        var policy = new EventMessageRetryPolicy(Options.Create(new ModuleEventBusOption()));
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync(() =>
        {
            attempts++;
            stopping.Cancel();
            throw new InvalidOperationException("Failed while stopping.");
        }, stopping.Token));
        Assert.Equal(1, attempts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.ExecuteAsync(
            () => throw new InvalidOperationException("Must not run after shutdown."), stopping.Token));
    }

    [Fact]
    public void Constructor_WhenRetryCountIsNegative_ShouldRejectConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventMessageRetryPolicy(
            Options.Create(new ModuleEventBusOption { MessageRetryCount = -1 })));
    }
}
