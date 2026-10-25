using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Monica.UI.Shared.Support;

/// <summary>
/// Owns clipboard admission and feedback for one viewer. The runtime and logger are borrowed.
/// Source replacement invalidates feedback; retirement drains the actual write without claiming
/// to cancel a browser clipboard operation or disposing either borrowed dependency.
/// </summary>
internal sealed class TextCopySession(IJSRuntime runtime, ILogger logger) : IAsyncDisposable
{
    private readonly IJSRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly object _sync = new();
    private string? _source;
    private long _generation;
    private TextCopyFeedback _feedback;
    private TaskCompletionSource? _active;
    private Task? _retirement;
    private bool _retired;

    internal TextCopyState State
    {
        get
        {
            lock (_sync)
                return new(!_retired && _active is null && !string.IsNullOrEmpty(_source), _active is not null, _feedback, _retired);
        }
    }

    /// <summary>Records exact source and clears feedback only when its ordinal value changes.</summary>
    internal bool SetSource(string? source)
    {
        lock (_sync)
        {
            if (_retired || string.Equals(_source, source, StringComparison.Ordinal)) return false;
            _source = source;
            _generation++;
            _feedback = TextCopyFeedback.None;
            return true;
        }
    }

    /// <summary>
    /// Captures the current nonempty source before invoking JavaScript. Repeated admission joins
    /// the existing write; no copy is queued. The completion remains owned until its producer settles.
    /// </summary>
    internal Task CopyAsync()
    {
        TaskCompletionSource completion;
        string payload;
        long generation;
        lock (_sync)
        {
            if (_retired || string.IsNullOrEmpty(_source)) return Task.CompletedTask;
            if (_active is not null) return _active.Task;
            payload = _source;
            generation = _generation;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _active = completion;
            _feedback = TextCopyFeedback.None;
        }

        // Reserve drainage before a synchronous runtime can re-enter source replacement or retirement.
        _ = ObserveCopyAsync(payload, generation, completion);
        return completion.Task;
    }

    private async Task ObserveCopyAsync(string payload, long generation, TaskCompletionSource completion)
    {
        try
        {
            var copied = false;
            try
            {
                var producer = _runtime.InvokeAsync<bool>("MoClipboard.copyText", payload).AsTask();
                copied = await producer;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not copy text to the clipboard.");
            }

            lock (_sync)
            {
                if (!_retired && generation == _generation)
                    _feedback = copied ? TextCopyFeedback.Copied : TextCopyFeedback.Failed;
                _active = null;
                completion.TrySetResult();
            }
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _active = null;
                completion.TrySetException(exception);
            }
        }
    }

    /// <summary>Stops admission and shares one retirement completion that drains the actual write.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _retired = true;
            _feedback = TextCopyFeedback.None;
            _retirement ??= _active?.Task ?? Task.CompletedTask;
            return new(_retirement);
        }
    }
}

internal readonly record struct TextCopyState(bool CanCopy, bool IsCopying, TextCopyFeedback Feedback, bool IsRetired);

internal enum TextCopyFeedback
{
    None,
    Copied,
    Failed
}
