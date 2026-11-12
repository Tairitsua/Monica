using System.Runtime.CompilerServices;
using Monica.AI.Abstractions;
using Monica.AI.Chat.Abstractions;
using Monica.AI.Chat.Models;
using Monica.AI.Models;
using Monica.AI.Services;
using Monica.AI.Services.Support;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.AI.Facades;

/// <summary>Scoped host/UI boundary for isolated conversations, streaming execution, and context controls.</summary>
public sealed class ChatFacade
{
    private readonly AIChatService _chatService;
    private readonly IAIProviderFactory _providerFactory;
    private readonly IChatHistoryPartitionResolver _partitionResolver;

    internal ChatFacade(AIChatService chatService, IAIProviderFactory providerFactory,
        IChatHistoryPartitionResolver partitionResolver)
    {
        _chatService = chatService;
        _providerFactory = providerFactory;
        _partitionResolver = partitionResolver;
    }

    /// <summary>Current host-shared provider metadata, excluding credentials.</summary>
    public IReadOnlyList<AIProviderInfo> GetProviders() => _providerFactory.GetAllProviderInfos();
    /// <summary>Current host default provider metadata.</summary>
    public AIProviderInfo? GetDefaultProvider() => _providerFactory.GetDefaultProviderInfo();

    /// <summary>Changes choices for the next new turn without mutating an in-flight request.</summary>
    public Result UpdateSettings(ChatSession session, ChatSessionSettings settings)
        => Apply(() => _chatService.UpdateSettings(session, settings));

    /// <summary>Changes the conversation display title.</summary>
    public Result Rename(ChatSession session, string title) => Apply(() => session.Rename(title));

    /// <summary>Changes runtime tool context for subsequent execution.</summary>
    public Result UpdateRuntimeContext(ChatSession session, AIChatRuntimeContext runtimeContext)
        => Apply(() => session.SetRuntimeContext(runtimeContext));

    /// <summary>Creates an isolated conversation with choices applied when its first turn begins.</summary>
    public async Task<Result<ChatSession>> CreateSessionAsync(string? providerId = null, string? modelName = null,
        string? systemPrompt = null, AIChatRuntimeContext? runtimeContext = null, string? reasoningLevel = null,
        string? title = null, CancellationToken ct = default)
    {
        try
        {
            var partition = await _partitionResolver.ResolveAsync(ct);
            var session = await _chatService.CreateSessionAsync(providerId, modelName, systemPrompt, runtimeContext, reasoningLevel, title, ct);
            session.BindPartition(partition);
            return Result.Success(session);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure<ChatSession>(ex.GetMessageRecursively()); }
    }

    /// <summary>Sends plain text and returns ordered Monica-owned stream events.</summary>
    public IAsyncEnumerable<Result<ChatStreamEvent>> SendMessageStreamingAsync(ChatSession session, string message, CancellationToken ct = default)
        => SendMessageStreamingAsync(session, ChatUserInput.FromText(message), ct);

    /// <summary>Sends ordered text and persisted attachments after verifying current conversation ownership.</summary>
    public IAsyncEnumerable<Result<ChatStreamEvent>> SendMessageStreamingAsync(ChatSession session, ChatUserInput input, CancellationToken ct = default)
        => ProcessAsync(session, () => _chatService.SendMessageStreamingAsync(session, input, ct), ct);

    /// <summary>Edits a user turn, discards its following branch, and generates a new response.</summary>
    public IAsyncEnumerable<Result<ChatStreamEvent>> EditMessageAsync(ChatSession session, string messageId, string newContent, CancellationToken ct = default)
        => ProcessAsync(session, () => _chatService.SendMessageStreamingAsync(session, session.RewindForEdit(messageId, newContent), ct), ct);

    /// <summary>Retries a turn with its original text and attachment references under the current settings.</summary>
    public IAsyncEnumerable<Result<ChatStreamEvent>> RetryMessageAsync(ChatSession session, string messageId, CancellationToken ct = default)
        => ProcessAsync(session, () => _chatService.SendMessageStreamingAsync(session, session.RewindForRetry(messageId), ct), ct);

    /// <summary>Continues the original run after an approval decision; opaque SDK objects remain private.</summary>
    public IAsyncEnumerable<Result<ChatStreamEvent>> ContinueApprovalAsync(ChatSession session, string approvalId,
        bool approved, string? reason = null, CancellationToken ct = default)
        => ProcessAsync(session, () => _chatService.ContinueApprovalStreamingAsync(session,
            session.TakePendingApproval(approvalId).CreateResponse(approved, reason), ct), ct);

    /// <summary>Summarizes complete older turns while retaining the original transcript and execution ledger.</summary>
    public async Task<Result<ChatCompactionResult>> CompactAsync(ChatSession session, CancellationToken ct = default)
    {
        try
        {
            session.BindPartition(await _partitionResolver.ResolveAsync(ct));
            return Result.Success(await _chatService.CompactAsync(session, ct));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure<ChatCompactionResult>(ex.GetMessageRecursively()); }
    }

    /// <summary>Cancels active generation or a paused approval and releases the runtime without executing pending tools.</summary>
    public async Task<Result> CancelAsync(ChatSession session, CancellationToken ct = default)
    {
        try
        {
            session.BindPartition(await _partitionResolver.ResolveAsync(ct));
            await _chatService.CancelAsync(session);
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(ex.GetMessageRecursively()); }
    }

    /// <summary>Records a failure in the UI consumer against the latest turn.</summary>
    public Result RecordError(ChatSession session, string error) => Apply(() => session.RecordError(error));

    private async IAsyncEnumerable<Result<ChatStreamEvent>> ProcessAsync(ChatSession session,
        Func<IAsyncEnumerable<ChatStreamEvent>> createStream, [EnumeratorCancellation] CancellationToken ct)
    {
        IAsyncEnumerator<ChatStreamEvent>? enumerator = null;
        string? error = null;
        try
        {
            session.BindPartition(await _partitionResolver.ResolveAsync(ct));
            enumerator = createStream().GetAsyncEnumerator(ct);
        }
        catch (Exception ex) { error = ex.GetMessageRecursively(); }
        if (error is not null) { yield return Result.Failure<ChatStreamEvent>(error); yield break; }

        var streamEnumerator = enumerator!;
        await using (streamEnumerator)
        {
            while (true)
            {
                bool hasNext = false;
                try { hasNext = await streamEnumerator.MoveNextAsync(); }
                catch (OperationCanceledException) { yield break; }
                catch (Exception ex) { error = ex.GetMessageRecursively(); }
                if (error is not null) { yield return Result.Failure<ChatStreamEvent>(error); yield break; }
                if (!hasNext) yield break;
                yield return Result.Success<ChatStreamEvent>(streamEnumerator.Current);
            }
        }
    }

    private static Result Apply(Action action)
    {
        try { action(); return Result.Success(); }
        catch (Exception ex) { return Result.Failure(ex.GetMessageRecursively()); }
    }
}
