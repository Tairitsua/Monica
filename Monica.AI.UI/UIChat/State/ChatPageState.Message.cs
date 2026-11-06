using System.Diagnostics;
using Microsoft.AspNetCore.Components.Forms;
using Monica.AI.Chat.Models;
using Monica.AI.Models;
using Monica.AI.UI.UIChat.Components;
using Monica.AI.UI.UIChat.Models;
using Monica.AI.UI.UIChat.Support;
using Monica.Core.Results;
using MudBlazor;

namespace Monica.AI.UI.UIChat.State;

public sealed partial class ChatPageState
{
    /// <summary>Submits text and staged durable attachments in their selected order.</summary>
    public Task SendMessageAsync(ChatSendRequest request)
        => RunOperationAsync(async ct =>
        {
            if (string.IsNullOrWhiteSpace(request.Message) && _attachments.Count == 0) return;
            var session = CurrentSession ?? await TryCreateSessionAsync();
            if (session is null) return;
            _operationSession = session;
            ct.ThrowIfCancellationRequested();
            if (_attachments.Any(item => item.Kind == ChatAttachmentKind.Image) && CurrentModel?.SupportsImage != true)
            {
                SetError(localizer["Workbench:ImageUnsupported"]);
                return;
            }
            if (_attachments.Any(item => item.Kind == ChatAttachmentKind.Document && !item.HasExtractedText) && CurrentModel?.SupportsDocuments != true)
            {
                SetError(localizer["Workbench:DocumentUnsupported"]);
                return;
            }
            var runtimeResult = chatFacade.UpdateRuntimeContext(session, BuildRuntimeContext(SelectedKnowledgeBaseIds));
            if (runtimeResult.IsFailure())
            { SetError(runtimeResult.Message); return; }
            if (session.Messages.Count == 0)
                _ = chatFacade.Rename(session, ChatProviderResolver.GenerateSessionTitle(
                    string.IsNullOrWhiteSpace(request.Message) ? _attachments[0].FileName : request.Message));
            var parts = new List<ChatContentPart>();
            if (!string.IsNullOrWhiteSpace(request.Message)) parts.Add(ChatContentPart.FromText(request.Message.Trim()));
            parts.AddRange(_attachments.Select(ChatContentPart.FromAttachment));
            request.Accepted = true;
            _attachments.Clear();
            await ConsumeStreamAsync(session, chatFacade.SendMessageStreamingAsync(session, new ChatUserInput { Parts = parts }, ct), ct);
        });

    /// <summary>Edits a user turn and regenerates from that point.</summary>
    public Task EditMessageAsync((AIChatMessage Message, string NewContent) edit)
        => RunOperationAsync(async ct =>
        {
            if (CurrentSession is { } session)
                await ConsumeStreamAsync(session, chatFacade.EditMessageAsync(session, edit.Message.Id, edit.NewContent, ct), ct);
        });

    /// <summary>Regenerates the selected assistant turn using current settings.</summary>
    public Task RetryMessageAsync(AIChatMessage message)
        => RunOperationAsync(async ct =>
        {
            if (CurrentSession is { } session)
                await ConsumeStreamAsync(session, chatFacade.RetryMessageAsync(session, message.Id, ct), ct);
        });

    /// <summary>Compacts context while preserving the transcript and execution records.</summary>
    public Task CompactAsync() => RunOperationAsync(async ct =>
    {
        if (CurrentSession is not { } session) return;
        var result = await chatFacade.CompactAsync(session, ct);
        var error = result.WithoutData();
        var compacted = result.Data;
        if (result.IsFailure() || compacted is null) SetError(error.Message);
        else if (!_disposed) snackbar.Add(compacted.Applied
            ? localizer["Workbench:CompactionSummary", compacted.BeforeTokens, compacted.AfterTokens]
            : compacted.Reason ?? localizer["Workbench:NothingToCompact"], Severity.Info);
    });

    /// <summary>Requests cancellation; the recorded interrupted work remains inspectable.</summary>
    public async Task CancelAsync()
    {
        if (_requestCancellation is not null) await _requestCancellation.CancelAsync();
    }

    private Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (IsHistoryBusy || _disposed || HasHistoryConflict || HasHistoryLoadFailure) return Task.CompletedTask;
        IsSending = true;
        return _operations.RunAsync(_ => RunOperationCoreAsync(operation));
    }

    private async Task RunOperationCoreAsync(Func<CancellationToken, Task> operation)
    {
        IsSending = true;
        _operationSession = CurrentSession;
        ErrorMessage = null;
        _requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (_options.RequestTimeoutMs > 0) _requestCancellation.CancelAfter(_options.RequestTimeoutMs);
        NotifyStateChanged();
        try { await operation(_requestCancellation.Token); }
        catch (OperationCanceledException)
        {
            if (_operationSession is { } session) _ = await chatFacade.CancelAsync(session, CancellationToken.None);
        }
        catch (Exception exception) { SetError(exception.Message); }
        finally
        {
            // Cancellation stops generation, but saving its terminal state must still complete.
            try
            {
                if (_operationSession is { } session) await workspace.SaveSessionAsync(session);
            }
            catch (Exception exception) { SetError(exception.Message); }
            finally
            {
                IsSending = false;
                _requestCancellation.Dispose();
                _requestCancellation = null;
                _operationSession = null;
                NotifyStateChanged();
            }
        }
    }

    private async Task ConsumeStreamAsync(ChatSession session, IAsyncEnumerable<Result<ChatStreamEvent>> stream, CancellationToken ct)
    {
        ChatApprovalRequestEvent? approval = null;
        var paintClock = Stopwatch.StartNew();
        var checkpointClock = Stopwatch.StartNew();
        var checkpointed = false;
        var stepStatuses = new Dictionary<string, ChatExecutionStatus>(StringComparer.Ordinal);
        await foreach (var result in stream.WithCancellation(ct))
        {
            var error = result.WithoutData();
            var update = result.Data;
            if (result.IsFailure()) { SetError(error.Message); return; }
            approval = update as ChatApprovalRequestEvent ?? approval;
            var boundary = update is ChatApprovalRequestEvent;
            if (update is ChatStepChangedEvent changed)
            {
                boundary |= !stepStatuses.TryGetValue(changed.Step.Id, out var previous) || previous != changed.Step.Status;
                stepStatuses[changed.Step.Id] = changed.Step.Status;
            }
            // Await each checkpoint in the single stream consumer. Repeated partial events are coalesced,
            // while request/tool starts and terminal states become durable before presentation continues.
            if (!checkpointed || boundary || checkpointClock.ElapsedMilliseconds >= 1500)
            {
                await workspace.SaveSessionAsync(session, ct);
                checkpointed = true;
                checkpointClock.Restart();
                if (workspace.IsConflicted) await CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
            // Stream facts are already owned by the session. Batch only presentation invalidation.
            if (paintClock.ElapsedMilliseconds >= 40 || update is ChatCompletedEvent or ChatApprovalRequestEvent)
            {
                NotifyStateChanged();
                paintClock.Restart();
            }
        }
        ct.ThrowIfCancellationRequested();
        if (approval is null || _disposed) return;
        var resultDialog = await ShowDialogAsync<ExternalScriptApprovalDialog>(localizer["Chat:Approval:Title"],
            new DialogParameters { [nameof(ExternalScriptApprovalDialog.Request)] = approval },
            new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium, CloseOnEscapeKey = false }, ct);
        ct.ThrowIfCancellationRequested();
        if (_disposed) return;
        if (resultDialog is not { Canceled: false })
        {
            _ = await chatFacade.CancelAsync(session, CancellationToken.None);
            return;
        }
        var approved = resultDialog.Data is true;
        await ConsumeStreamAsync(session, chatFacade.ContinueApprovalAsync(session, approval.ApprovalId, approved,
            approved ? localizer["Chat:Approval:ApprovedReason"] : localizer["Chat:Approval:RejectedReason"], ct), ct);
    }

    /// <summary>Persists selected files before accepting them into a request.</summary>
    public Task UploadAttachmentsAsync(InputFileChangeEventArgs args)
    {
        if (IsHistoryBusy || _disposed || HasHistoryConflict || HasHistoryLoadFailure) return Task.CompletedTask;
        IsUploading = true;
        return _operations.RunAsync(_ => UploadAttachmentsCoreAsync(args));
    }

    private async Task UploadAttachmentsCoreAsync(InputFileChangeEventArgs args)
    {
        IsUploading = true;
        NotifyStateChanged();
        try
        {
            var session = CurrentSession ?? await TryCreateSessionAsync();
            if (session is null) return;
            foreach (var file in args.GetMultipleFiles(10))
            {
                await using var data = file.OpenReadStream(aiOptions.Value.MaxChatAttachmentBytes, _lifetime.Token);
                var result = await attachmentFacade.UploadAsync(session.SessionId, file.Name, file.ContentType, data, _lifetime.Token);
                var error = result.WithoutData();
                var attachment = result.Data;
                if (result.IsFailure() || attachment is null) { SetError(error.Message); continue; }
                if (_disposed) return;
                _attachments.Add(attachment);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { SetError(exception.Message); }
        finally { IsUploading = false; NotifyStateChanged(); }
    }

    /// <summary>Removes an attachment that has not yet been submitted.</summary>
    public async Task RemoveAttachmentAsync(ChatAttachmentReference attachment) => _ = await RunCommandAsync(() => RemoveAttachmentCoreAsync(attachment));

    private async Task<bool> RemoveAttachmentCoreAsync(ChatAttachmentReference attachment)
    {
        if (_disposed || !_attachments.Contains(attachment) || CurrentSessionId is not { } id) return false;
        var result = await attachmentFacade.DeleteAsync(id, attachment.Id, _lifetime.Token);
        var error = result;
        if (result.IsFailure()) { SetError(error.Message); return false; }
        if (_disposed || CurrentSessionId != id) return false;
        _attachments.Remove(attachment);
        NotifyStateChanged();
        return true;
    }
    private async Task<bool> DiscardAttachmentsAsync()
    {
        foreach (var attachment in _attachments.ToArray())
            if (!await RemoveAttachmentCoreAsync(attachment)) return false;
        return true;
    }
}
