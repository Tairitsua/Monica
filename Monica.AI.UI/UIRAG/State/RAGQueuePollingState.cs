using Monica.AI.KnowledgeBase.Facades;
using Monica.AI.KnowledgeBase.Models;
using Monica.AI.UI.Shared.Support;
using Monica.Core.Results;

namespace Monica.AI.UI.UIRAG.State;

/// <summary>Page-owned queue polling. Stop cancels a generation; retirement drains actual reads.</summary>
/// <remarks>The document facade is borrowed. A stopped or replaced poll cannot publish a late inventory,
/// including a later visit to the same base. The cancellation source is released only after its worker drains.</remarks>
public sealed class RAGQueuePollingState(KnowledgeDocumentFacade knowledgeDocumentFacade) : IAsyncDisposable
{
    private readonly AIAdministrationOperationLifetime _lifetime = new();
    private readonly List<Task> _owned = [];
    private readonly List<PollRun> _runs = [];
    private PollRun? _run;
    private long _generation;
    private DateTimeOffset _lastProgressRefreshAtUtc = DateTimeOffset.MinValue;
    private int _progressRefreshInFlight;

    /// <summary>Awaited publication of a successful receipt for its originating knowledge base.</summary>
    public event Func<string, long, IReadOnlyList<DocumentQueueItem>, Task>? QueueRefreshed;
    /// <summary>Awaited publication of a failed receipt for its originating knowledge base.</summary>
    public event Func<string, long, string, Task>? QueueRefreshFailed;
    /// <summary>Raised once after a successful final inventory read when this poll's active work has settled.</summary>
    /// <remarks>The receipt retains its generation so consumers can reject late validation or renderer work.</remarks>
    public event Func<string, long, Task>? QueueSettled;

    /// <summary>Whether a queued renderer receipt still belongs to the active polling generation.</summary>
    public bool IsCurrentGeneration(long generation) => !_lifetime.IsRetired && generation == _generation;

    /// <summary>Starts one generation while work exists; replacing it cancels the previous owner.</summary>
    public void EnsurePolling(string knowledgeBaseId, Func<bool> hasActiveWork)
    {
        if (_lifetime.IsRetired) return;
        if (string.IsNullOrWhiteSpace(knowledgeBaseId) || !hasActiveWork()) { Stop(); return; }
        if (_run is { } active && active.Id == knowledgeBaseId && active.Task is { IsCompleted: false }) return;
        Stop();
        var run = new PollRun(knowledgeBaseId, _generation);
        _runs.Add(run);
        _run = run;
        run.Task = _lifetime.RunAsync(async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, run.Token);
            try
            {
                while (!linked.IsCancellationRequested && IsCurrent(run))
                {
                    await RefreshQueueAsync(run.Id, run.Generation, linked.Token);
                    if (!IsCurrent(run)) break;
                    if (!hasActiveWork())
                    {
                        // Completion can race the preceding read. Capture persisted final rows before validation.
                        var finalReadSucceeded = await RefreshQueueAsync(run.Id, run.Generation, linked.Token);
                        if (!IsCurrent(run)) break;
                        // A new operation may have begun during the final read; retain the same loop for it.
                        if (hasActiveWork()) continue;
                        if (finalReadSucceeded && QueueSettled is { } settled)
                            foreach (Func<string, long, Task> callback in settled.GetInvocationList())
                                await callback(run.Id, run.Generation);
                        break;
                    }
                    await Task.Delay(700, linked.Token);
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(_run, run)) _run = null;
            }
        });
        lock (_owned)
        {
            _owned.RemoveAll(completed => completed.IsCompletedSuccessfully);
            _owned.Add(run.Task);
        }
    }

    /// <summary>Performs a throttled owned refresh for the current poll generation.</summary>
    public async Task RefreshFromProgressAsync(string knowledgeBaseId)
    {
        if (_lifetime.IsRetired || DateTimeOffset.UtcNow - _lastProgressRefreshAtUtc < TimeSpan.FromSeconds(1)
            || Interlocked.Exchange(ref _progressRefreshInFlight, 1) != 0) return;
        var generation = _generation;
        try
        {
            _lastProgressRefreshAtUtc = DateTimeOffset.UtcNow;
            await _lifetime.RunAsync(async token => { await RefreshQueueAsync(knowledgeBaseId, generation, token); });
        }
        catch (OperationCanceledException) when (_lifetime.IsRetired) { }
        finally { Interlocked.Exchange(ref _progressRefreshInFlight, 0); }
    }

    /// <summary>Cancels this generation without abandoning the actual producer or releasing its source early.</summary>
    public void Stop()
    {
        _generation++;
        var run = _run;
        _run = null;
        run?.Cancellation.Cancel();
    }

    private bool IsCurrent(PollRun run) => !_lifetime.IsRetired && run.Generation == _generation && ReferenceEquals(_run, run);

    private async Task<bool> RefreshQueueAsync(string id, long generation, CancellationToken token)
    {
        // The facade has no token overload. Retirement retains and drains the actual read.
        var result = await knowledgeDocumentFacade.GetDocumentInventoryAsync(id);
        if (token.IsCancellationRequested || _lifetime.IsRetired || generation != _generation) return false;
        var error = result.WithoutData();
        var queue = result.Data;
        if (result.IsFailure() || queue is null)
        {
            if (QueueRefreshFailed is { } failed)
                foreach (Func<string, long, string, Task> callback in failed.GetInvocationList())
                    await callback(id, generation, error.Message ?? "Failed to load document queue.");
            return false;
        }
        else if (QueueRefreshed is { } refreshed)
            foreach (Func<string, long, IReadOnlyList<DocumentQueueItem>, Task> callback in refreshed.GetInvocationList())
                await callback(id, generation, queue);
        return !token.IsCancellationRequested && IsCurrentGeneration(generation);
    }

    /// <summary>Cancels and drains admitted polling and progress reads before resource release.</summary>
    public async ValueTask DisposeAsync()
    {
        var retirement = _lifetime.RetireAsync().AsTask();
        Stop();
        var failures = new List<Exception>();
        try { await retirement; } catch (Exception exception) { failures.Add(exception); }
        Task[] owned;
        lock (_owned) owned = _owned.ToArray();
        foreach (var task in owned)
        {
            try { await task; } catch (Exception exception) { failures.Add(exception); }
        }
        foreach (var run in _runs) run.Cancellation.Dispose();
        if (failures.Count > 0) throw new AggregateException("RAG polling retirement failed.", failures);
    }

    private sealed class PollRun
    {
        internal PollRun(string id, long generation)
        {
            Id = id;
            Generation = generation;
            Token = Cancellation.Token;
        }
        internal string Id { get; }
        internal long Generation { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal CancellationToken Token { get; }
        internal Task? Task { get; set; }
    }
}
