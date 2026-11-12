using Monica.AI.Models;
using Monica.Core.Results;
using Monica.AI.UI.Shared.Components;
using MudBlazor;

namespace Monica.AI.UI.UIChat.State;

public sealed partial class ChatPageState
{
    private async Task EnsureSessionExistsAsync()
    {
        if (workspace.HasLoadFailure) return;
        if (workspace.CurrentSession is not null) return;
        if (workspace.Sessions.FirstOrDefault() is { } next)
            _ = await workspace.SelectSessionAsync(next.SessionId, _lifetime.Token);
        else if (CurrentProviderId is not null)
            _ = await TryCreateSessionAsync();
    }

    /// <summary>Creates and selects a durable conversation.</summary>
    public async Task CreateNewSessionAsync() => _ = await RunHistoryOperationAsync(async () =>
    {
        if (!await DiscardAttachmentsAsync()) return false;
        if (await TryCreateSessionAsync() is null) return false;
        InspectedStep = null;
        return true;
    });

    /// <summary>Selects a conversation within the current identity partition.</summary>
    public async Task SelectSessionAsync(string id) => _ = await RunHistoryOperationAsync(async () =>
    {
        if (!await DiscardAttachmentsAsync()) return false;
        if (await workspace.SelectSessionAsync(id, _lifetime.Token) is null) return false;
        InspectedStep = null;
        ErrorMessage = null;
        return true;
    });

    /// <summary>Toggles a conversation pin without altering its transcript.</summary>
    public async Task TogglePinAsync(string id) => _ = await RunHistoryOperationAsync(async () =>
    {
        var summary = Sessions.FirstOrDefault(item => item.SessionId == id);
        return summary is not null && await workspace.SetPinnedAsync(id, !summary.IsPinned, _lifetime.Token);
    });

    /// <summary>Archives a conversation while retaining its transcript and attachments.</summary>
    public async Task ArchiveSessionAsync(string id) => _ = await RunHistoryOperationAsync(async () =>
    {
        if (id == CurrentSessionId && !await DiscardAttachmentsAsync()) return false;
        if (!await workspace.SetArchivedAsync([id], true, _lifetime.Token)) return false;
        InspectedStep = null;
        return true;
    });

    /// <summary>Restores selected archived conversations without changing the current selection.</summary>
    public Task<bool> RestoreArchivedSessionsAsync(IReadOnlyList<string> ids)
        => RunHistoryOperationAsync(() => workspace.SetArchivedAsync(ids, false, _lifetime.Token));

    /// <summary>Permanently removes the selection after the archive manager obtains explicit confirmation.</summary>
    public Task<bool> DeleteArchivedSessionsAsync(IReadOnlyList<string> ids)
        => RunHistoryOperationAsync(() => workspace.DeleteArchivedSessionsAsync(ids, _lifetime.Token));

    /// <summary>Explicitly discards conflicting in-memory edits and reloads durable history.</summary>
    public async Task ReloadHistoryAsync() => _ = await RunCommandAsync(async () =>
    {
        var confirmed = await ShowDialogAsync<AIAdministrationConfirmation>(localizer["Workbench:ReloadHistory"], new()
        {
            [nameof(AIAdministrationConfirmation.Title)] = localizer["Workbench:ReloadHistory"].Value,
            [nameof(AIAdministrationConfirmation.Description)] = localizer["Workbench:ReloadHistoryConfirm"].Value,
            [nameof(AIAdministrationConfirmation.ConfirmText)] = localizer["Workbench:ReloadHistory"].Value
        }, new() { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true }, _lifetime.Token);
        if (_disposed || confirmed is not { Canceled: false, Data: true }) return false;
        if (!await DiscardAttachmentsAsync()) return false;
        await workspace.ReloadAsync(_lifetime.Token);
        InspectedStep = null;
        return true;
    }, allowConflict: true);

    private Task<bool> RunHistoryOperationAsync(Func<Task<bool>> action, bool allowConflict = false)
    {
        if (_disposed || IsHistoryBusy || (HasHistoryConflict || HasHistoryLoadFailure) && !allowConflict) return Task.FromResult(false);
        IsHistoryUpdating = true;
        return _operations.RunAsync(_ => RunHistoryOperationCoreAsync(action));
    }

    private async Task<bool> RunHistoryOperationCoreAsync(Func<Task<bool>> action)
    {
        IsHistoryUpdating = true;
        NotifyStateChanged();
        try { return await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception exception) { SetError(exception.Message); return false; }
        finally { IsHistoryUpdating = false; NotifyStateChanged(); }
    }

    private async Task<ChatSession?> TryCreateSessionAsync()
    {
        if (_disposed || workspace.HasLoadFailure) return null;
        RefreshProviders();
        if (string.IsNullOrWhiteSpace(CurrentProviderId))
        {
            SetError(localizer["Provider:NoChatProvider"]);
            return null;
        }
        var result = await chatFacade.CreateSessionAsync(CurrentProviderId, CurrentModelName,
            systemPrompt: _options.DefaultSystemPrompt, runtimeContext: BuildRuntimeContext(SelectedKnowledgeBaseIds), ct: _lifetime.Token);
        var error = result.WithoutData();
        var session = result.Data;
        if (result.IsFailure() || session is null) { SetError(error.Message); return null; }
        if (_disposed) { await session.DisposeAsync(); return null; }
        await workspace.AddSessionAsync(session, _lifetime.Token);
        ErrorMessage = null;
        return session;
    }
}
