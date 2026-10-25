using Monica.UI.Shared.Support;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace Test.Monica.UI.Shared.Support;

public sealed class TextCopySessionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CopyAsync_WhenProducerSettles_ShouldPublishOnlyItsActualBooleanResult(bool copied)
    {
        var producer = NewCompletion();
        var runtime = new ControlledRuntime(_ => producer.Task);
        await using var session = CreateSession(runtime);
        const string source = "\r\n\tSource <tag> & 尾部 😀\r\n \t";
        session.SetSource(source);

        var copy = session.CopyAsync();
        session.State.IsCopying.Should().BeTrue();
        session.State.Feedback.Should().Be(TextCopyFeedback.None);
        session.State.CanCopy.Should().BeFalse();
        runtime.Calls.Should().ContainSingle().Which.Should().Be(new ClipboardCall("MoClipboard.copyText", source));

        producer.SetResult(copied);
        await copy;
        session.State.Feedback.Should().Be(copied ? TextCopyFeedback.Copied : TextCopyFeedback.Failed);
        session.State.IsCopying.Should().BeFalse();
        session.State.CanCopy.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData(" \t\r\n\r\n ", 1)]
    public async Task CopyAsync_WhenSourceIsAbsentOrWhitespace_ShouldSkipOnlyNullAndEmpty(string? source, int calls)
    {
        var runtime = new ControlledRuntime(_ => Task.FromResult(true));
        await using var session = CreateSession(runtime);
        session.SetSource(source);

        await session.CopyAsync();

        runtime.Calls.Should().HaveCount(calls);
        if (calls > 0) runtime.Calls[0].Payload.Should().Be(source);
    }

    [Fact]
    public async Task CopyAsync_WhenAnotherWriteIsPending_ShouldJoinItWithoutQueueingNewSource()
    {
        var producer = NewCompletion();
        var runtime = new ControlledRuntime(_ => producer.Task);
        await using var session = CreateSession(runtime);
        session.SetSource("first");
        var first = session.CopyAsync();
        session.SetSource("replacement");

        var second = session.CopyAsync();
        second.Should().BeSameAs(first);
        runtime.Calls.Should().ContainSingle().Which.Payload.Should().Be("first");

        producer.SetResult(true);
        await Task.WhenAll(first, second);
        session.State.Feedback.Should().Be(TextCopyFeedback.None);
        session.State.CanCopy.Should().BeTrue();
        runtime.Calls.Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetSource_WhenSourceReturnsToOriginalBeforeCompletion_ShouldRejectTheOldGeneration(bool copied)
    {
        var producer = NewCompletion();
        var runtime = new ControlledRuntime(_ => producer.Task);
        await using var session = CreateSession(runtime);
        session.SetSource("A");
        var copy = session.CopyAsync();

        session.SetSource("B");
        session.SetSource("A");
        producer.SetResult(copied);
        await copy;

        session.State.Feedback.Should().Be(TextCopyFeedback.None);
        runtime.Calls.Should().ContainSingle().Which.Payload.Should().Be("A");
    }

    [Fact]
    public async Task CopyAsync_WhenOldProducerThrowsAfterSourceReturns_ShouldObserveFailureWithoutPublishingIt()
    {
        var producer = NewCompletion();
        var runtime = new ControlledRuntime(_ => producer.Task);
        var logger = new RecordingLogger();
        await using var session = new TextCopySession(runtime, logger);
        session.SetSource("A");
        var copy = session.CopyAsync();
        session.SetSource("B");
        session.SetSource("A");
        var error = new JSException("Clipboard script unavailable.");

        producer.SetException(error);
        await copy;

        logger.Errors.Should().ContainSingle().Which.Should().BeSameAs(error);
        session.State.Feedback.Should().Be(TextCopyFeedback.None);
    }

    [Fact]
    public async Task CopyAsync_WhenRuntimeFailsSynchronously_ShouldObserveFailureAndAllowExplicitRetry()
    {
        var attempts = 0;
        var error = new JSException("Clipboard script unavailable.");
        var runtime = new ControlledRuntime(_ => ++attempts == 1 ? throw error : Task.FromResult(true));
        var logger = new RecordingLogger();
        await using var session = new TextCopySession(runtime, logger);
        session.SetSource("source");

        await session.CopyAsync();
        session.State.Feedback.Should().Be(TextCopyFeedback.Failed);
        logger.Errors.Should().ContainSingle().Which.Should().BeSameAs(error);

        await session.CopyAsync();
        session.State.Feedback.Should().Be(TextCopyFeedback.Copied);
        runtime.Calls.Should().HaveCount(2);
        session.SetSource("source").Should().BeFalse();
        session.State.Feedback.Should().Be(TextCopyFeedback.Copied);
        session.SetSource("new source").Should().BeTrue();
        session.State.Feedback.Should().Be(TextCopyFeedback.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_WhenWriteIsPending_ShouldDrainActualProducerAndRejectRetiredPublication(bool copied)
    {
        var producer = NewCompletion();
        var runtime = new ControlledRuntime(_ => producer.Task);
        var session = CreateSession(runtime);
        session.SetSource("source");
        var copy = session.CopyAsync();

        var retirement = session.DisposeAsync().AsTask();
        session.DisposeAsync().AsTask().Should().BeSameAs(retirement);
        retirement.IsCompleted.Should().BeFalse();
        session.State.IsRetired.Should().BeTrue();
        session.SetSource("retired source").Should().BeFalse();
        await session.CopyAsync();
        runtime.Calls.Should().ContainSingle();

        producer.SetResult(copied);
        await Task.WhenAll(copy, retirement);
        session.State.Feedback.Should().Be(TextCopyFeedback.None);
        session.State.IsCopying.Should().BeFalse();
        runtime.DisposeCount.Should().Be(0);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenLateWriteFailsUnexpectedly_ShouldDrainAndKeepFailureObserved()
    {
        var producer = NewCompletion();
        var runtime = new ControlledRuntime(_ => producer.Task);
        var logger = new RecordingLogger();
        var session = new TextCopySession(runtime, logger);
        session.SetSource("source");
        var copy = session.CopyAsync();
        var retirement = session.DisposeAsync().AsTask();
        var error = new InvalidOperationException("Unexpected runtime failure.");

        producer.SetException(error);
        await Task.WhenAll(copy, retirement);

        logger.Errors.Should().ContainSingle().Which.Should().BeSameAs(error);
        session.State.Feedback.Should().Be(TextCopyFeedback.None);
        runtime.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task CopyAsync_WhenRuntimeReentersRetirementBeforeReturningProducer_ShouldReserveDrainageFirst()
    {
        var producer = NewCompletion();
        TextCopySession? session = null;
        Task? retirement = null;
        var runtime = new ControlledRuntime(_ =>
        {
            session!.SetSource("replacement");
            retirement = session.DisposeAsync().AsTask();
            return producer.Task;
        });
        session = CreateSession(runtime);
        session.SetSource("captured source");

        var copy = session.CopyAsync();
        retirement.Should().NotBeNull();
        retirement!.IsCompleted.Should().BeFalse();
        runtime.Calls.Should().ContainSingle().Which.Payload.Should().Be("captured source");

        producer.SetResult(true);
        await Task.WhenAll(copy, retirement);
        session.State.Feedback.Should().Be(TextCopyFeedback.None);
        runtime.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task CopyAsync_WhenTwoViewersShareRuntime_ShouldKeepAdmissionFeedbackAndRetirementIndependent()
    {
        var firstProducer = NewCompletion();
        var secondProducer = NewCompletion();
        var runtime = new ControlledRuntime(source => source == "first" ? firstProducer.Task : secondProducer.Task);
        var first = CreateSession(runtime);
        await using var second = CreateSession(runtime);
        first.SetSource("first");
        second.SetSource("second");
        var firstCopy = first.CopyAsync();
        var secondCopy = second.CopyAsync();
        var retirement = first.DisposeAsync().AsTask();

        secondProducer.SetResult(true);
        await secondCopy;
        second.State.Feedback.Should().Be(TextCopyFeedback.Copied);
        retirement.IsCompleted.Should().BeFalse();

        firstProducer.SetResult(false);
        await Task.WhenAll(firstCopy, retirement);
        first.State.Feedback.Should().Be(TextCopyFeedback.None);
        second.State.Feedback.Should().Be(TextCopyFeedback.Copied);
    }

    private static TextCopySession CreateSession(IJSRuntime runtime) => new(runtime, NullLogger.Instance);
    private static TaskCompletionSource<bool> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record ClipboardCall(string Identifier, string Payload);

    private sealed class ControlledRuntime(Func<string, Task<bool>> write) : IJSRuntime, IAsyncDisposable
    {
        internal List<ClipboardCall> Calls { get; } = [];
        internal int DisposeCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var payload = (string)args![0]!;
            Calls.Add(new(identifier, payload));
            return new(ConvertResultAsync<TValue>(write(payload)));
        }

        private static async Task<TValue> ConvertResultAsync<TValue>(Task<bool> producer) => (TValue)(object)await producer;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        internal List<Exception> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Errors.Add(exception);
        }
    }
}
