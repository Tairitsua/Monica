using Monica.Core.Results;
using Monica.UI.UIDiffHighlight.Facades;
using Monica.UI.UIDiffHighlight.Models;

namespace Monica.UI.UIDiffHighlight.State;

/// <summary>
/// Serializes real Diff work for one component visit. Callers own automatic/manual admission,
/// rendering, and empty-input policy. Retirement drains computation without claiming cancellation.
/// </summary>
internal sealed class DiffComparisonController(Func<DiffComparisonInput, Task<Result<DiffHighlightResult>>> compare) : IAsyncDisposable
{
    private readonly Func<DiffComparisonInput, Task<Result<DiffHighlightResult>>> _compare = compare
        ?? throw new ArgumentNullException(nameof(compare));
    private readonly object _sync = new();
    private DiffComparisonInput? _draft;
    private long _revision;
    private DiffComparisonEvidence? _accepted;
    private DiffComparisonFailure? _failure;
    private Request? _active;
    private Request? _pending;
    private TaskCompletionSource? _drain;
    private bool _retired;

    /// <summary>The facade is borrowed; this visit never disposes it.</summary>
    internal static DiffComparisonController ForFacade(DiffHighlightFacade facade) => new(input =>
        facade.HighlightAsync(input.OriginText, input.NewText, input.Options?.ToOptions()));

    internal DiffComparisonState State
    {
        get
        {
            lock (_sync)
                return new(_revision, _draft, _accepted, _failure, _drain is not null, _pending is not null, _retired);
        }
    }

    /// <summary>
    /// Records draft values without requesting work or clearing previous evidence. A changed draft
    /// invalidates outstanding acceptance and removes a queued comparison of the previous draft.
    /// </summary>
    internal bool SetDraft(DiffComparisonInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_sync)
        {
            if (_retired || _draft == input) return false;
            _draft = input;
            _revision++;
            _pending = null;
            return true;
        }
    }

    /// <summary>
    /// Requests the current draft and awaits the actual serial work batch, including its latest
    /// coalesced request. An identical active request is joined. No request is completed by a busy
    /// drop; superseded queued drafts are not computed. Inspect State after awaiting for acceptance.
    /// </summary>
    internal Task RequestAsync()
    {
        TaskCompletionSource completion;
        var start = false;
        lock (_sync)
        {
            if (_retired) return Task.CompletedTask;
            if (_draft is null) throw new InvalidOperationException("Set the Diff draft before requesting a comparison.");
            if (_active?.Revision != _revision) _pending = new(_revision, _draft);
            if (_drain is null)
            {
                _drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
                start = true;
            }
            completion = _drain;
        }
        // Publish the completion reservation before a synchronous facade/delegate can finish.
        if (start) _ = RunAsync(completion);
        return completion.Task;
    }

    /// <summary>
    /// Clears evidence for the wrapper's deliberate both-empty skip. The page can instead request
    /// an explicit empty comparison. Active work is invalidated and still drained normally.
    /// </summary>
    internal void ClearEvidence()
    {
        lock (_sync)
        {
            if (_retired) return;
            _revision++;
            _pending = null;
            _accepted = null;
            _failure = null;
        }
    }

    private async Task RunAsync(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                Request request;
                lock (_sync)
                {
                    if (_retired || _pending is null)
                    {
                        _active = null;
                        _drain = null;
                        completion.TrySetResult();
                        return;
                    }
                    request = _pending;
                    _pending = null;
                    _active = request;
                }

                DiffComparisonEvidence? accepted = null;
                DiffComparisonFailure? failure = null;
                try
                {
                    var result = await _compare(request.Input);
                    var error = result.WithoutData();
                    var data = result.Data;
                    if (result.IsFailure() || data is null)
                        failure = new(request.Revision, request.Input, error.Message ?? "The comparison returned no data.");
                    else
                        accepted = new(request.Revision, request.Input, data);
                }
                catch (Exception exception)
                {
                    failure = new(request.Revision, request.Input, exception.Message);
                }

                lock (_sync)
                {
                    if (!_retired && request.Revision == _revision)
                    {
                        if (accepted is not null)
                        {
                            _accepted = accepted;
                            _failure = null;
                        }
                        else _failure = failure;
                    }
                    _active = null;
                }
            }
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _active = null;
                _pending = null;
                _drain = null;
                completion.TrySetException(exception);
            }
        }
    }

    /// <summary>Stops admission and waits for actual work; no callback or notification is emitted.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _retired = true;
            _pending = null;
            return new(_drain?.Task ?? Task.CompletedTask);
        }
    }

    private sealed record Request(long Revision, DiffComparisonInput Input);
}
