using Monica.AI.Chat.Abstractions;
using Monica.AI.Chat.Models;
using Monica.AI.Models;
using Monica.AI.Services;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.AI.Chat.Facades;

/// <summary>Host-facing entry point for durable chat history operations.</summary>
public sealed class ChatHistoryFacade
{
    private readonly IChatHistoryProvider _historyProvider;
    private readonly IChatHistoryPartitionResolver _partitionResolver;
    private readonly AIChatService _chatService;

    internal ChatHistoryFacade(
        IChatHistoryProvider historyProvider,
        IChatHistoryPartitionResolver partitionResolver,
        AIChatService chatService)
    {
        _historyProvider = historyProvider;
        _partitionResolver = partitionResolver;
        _chatService = chatService;
    }

    /// <summary>Loads the persisted catalog and current-session selection.</summary>
    public async Task<Result<ChatHistoryCatalog>> GetCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            var partition = await _partitionResolver.ResolveAsync(ct);
            return Result.Success(await _historyProvider.GetCatalogAsync(partition, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<ChatHistoryCatalog>(ex.GetMessageRecursively());
        }
    }

    /// <summary>
    /// Loads a session's transcript as a runtime-lazy <see cref="ChatSession"/>.
    /// </summary>
    public async Task<Result<ChatSession?>> LoadSessionAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            var partition = await _partitionResolver.ResolveAsync(ct);
            var snapshot = await _historyProvider.LoadSessionAsync(partition, sessionId, ct);
            var session = snapshot is null
                ? null
                : _chatService.RestoreSession(snapshot, expectedSessionId: sessionId);
            session?.BindPartition(partition);
            return Result.Success<ChatSession?>(session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<ChatSession?>(ex.GetMessageRecursively());
        }
    }

    /// <summary>Saves a complete snapshot when <paramref name="expectedRevision"/> is current.</summary>
    public async Task<Result<ChatHistoryWriteResult>> SaveSessionAsync(
        ChatSession session,
        long expectedRevision,
        CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
            var partition = await _partitionResolver.ResolveAsync(ct);
            session.BindPartition(partition);
            var snapshot = await _chatService.CreateSnapshotAsync(session, ct);
            var result = await _historyProvider.SaveSessionAsync(
                partition,
                snapshot,
                expectedRevision,
                ct);
            if (result.IsPersisted)
            {
                session.MarkPersisted(result.Revision);
            }

            return Result.Success(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<ChatHistoryWriteResult>(ex.GetMessageRecursively());
        }
    }

    /// <summary>Deletes one persisted session when the expected catalog revision is current.</summary>
    public async Task<Result<ChatHistoryWriteResult>> DeleteSessionAsync(
        string sessionId,
        long expectedRevision,
        CancellationToken ct = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
            var partition = await _partitionResolver.ResolveAsync(ct);
            return Result.Success(await _historyProvider.DeleteSessionAsync(
                partition,
                sessionId,
                expectedRevision,
                ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<ChatHistoryWriteResult>(ex.GetMessageRecursively());
        }
    }

    /// <summary>
    /// Pins or unpins an active conversation in the current user/workspace without changing its transcript recency.
    /// Archived conversations must be restored before they can be pinned.
    /// </summary>
    public async Task<Result<ChatHistoryWriteResult>> SetPinnedAsync(
        string sessionId,
        bool isPinned,
        long expectedRevision,
        CancellationToken ct = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
            var partition = await _partitionResolver.ResolveAsync(ct);
            return Result.Success(await _historyProvider.SetPinnedAsync(partition, sessionId, isPinned, expectedRevision, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<ChatHistoryWriteResult>(ex.GetMessageRecursively());
        }
    }

    /// <summary>
    /// Archives or restores the complete selection atomically in the current user/workspace. Archiving clears pins
    /// and any matching current selection while preserving transcripts and attachments; restoration leaves them unpinned.
    /// </summary>
    public async Task<Result<ChatHistoryWriteResult>> SetArchivedAsync(
        IReadOnlyList<string> sessionIds,
        bool isArchived,
        long expectedRevision,
        CancellationToken ct = default)
    {
        try
        {
            ValidateSelection(sessionIds);
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
            var partition = await _partitionResolver.ResolveAsync(ct);
            return Result.Success(await _historyProvider.SetArchivedAsync(partition, sessionIds, isArchived, expectedRevision, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<ChatHistoryWriteResult>(ex.GetMessageRecursively());
        }
    }

    /// <summary>
    /// Permanently deletes selected archived conversations and their attachments in the current user/workspace.
    /// Active or unknown IDs reject the complete request. A successful result with a warning means the catalog
    /// deletion committed but residual physical files still require the provider's automatic cleanup retry.
    /// </summary>
    public async Task<Result<ChatHistoryWriteResult>> DeleteArchivedSessionsAsync(
        IReadOnlyList<string> sessionIds,
        long expectedRevision,
        CancellationToken ct = default)
    {
        try
        {
            ValidateSelection(sessionIds);
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
            var partition = await _partitionResolver.ResolveAsync(ct);
            return Result.Success(await _historyProvider.DeleteArchivedSessionsAsync(partition, sessionIds, expectedRevision, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<ChatHistoryWriteResult>(ex.GetMessageRecursively());
        }
    }

    /// <summary>Clears all persisted sessions when the expected catalog revision is current.</summary>
    public async Task<Result<ChatHistoryWriteResult>> ClearAsync(
        long expectedRevision,
        CancellationToken ct = default)
    {
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
            var partition = await _partitionResolver.ResolveAsync(ct);
            return Result.Success(await _historyProvider.ClearAsync(partition, expectedRevision, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<ChatHistoryWriteResult>(ex.GetMessageRecursively());
        }
    }

    /// <summary>Persists the current-session selection using optimistic concurrency.</summary>
    public async Task<Result<ChatHistoryWriteResult>> SetCurrentSessionAsync(
        string? sessionId,
        long expectedRevision,
        CancellationToken ct = default)
    {
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
            var partition = await _partitionResolver.ResolveAsync(ct);
            return Result.Success(await _historyProvider.SetCurrentSessionAsync(
                partition,
                string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
                expectedRevision,
                ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<ChatHistoryWriteResult>(ex.GetMessageRecursively());
        }
    }

    private static void ValidateSelection(IReadOnlyList<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        if (sessionIds.Count == 0) throw new ArgumentException("Choose at least one conversation.", nameof(sessionIds));
        foreach (var id in sessionIds) ArgumentException.ThrowIfNullOrWhiteSpace(id);
    }
}
