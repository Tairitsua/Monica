using Monica.AI.Models;
using Monica.AI.UI.UIChat.Support;
using Monica.AI.UI.UIProvider.Components;
using Monica.Core.Results;
using MudBlazor;

namespace Monica.AI.UI.UIChat.State;

public sealed partial class ChatPageState
{
    /// <summary>Atomically selects a provider and model for subsequent requests.</summary>
    public async Task SelectModelAsync((string ProviderId, string ModelName) selection) => _ = await RunCommandAsync(async () =>
    {
        RefreshProviders();
        var provider = ChatProviderResolver.FindProvider(Providers, selection.ProviderId);
        if (provider is not { IsValid: true } || !ChatProviderResolver.GetChatModels(provider).Any(model => model.ModelName == selection.ModelName)) return false;
        if (CurrentSession is { } session && !await SaveSettingsCoreAsync(session, session.Settings with { ProviderId = selection.ProviderId, ModelName = selection.ModelName, ReasoningLevel = null })) return false;
        if (_disposed) return false;
        DefaultProviderId = selection.ProviderId;
        DefaultModelName = selection.ModelName;
        NotifyStateChanged();
        return true;
    });

    /// <summary>Persists a model-defined reasoning level for the next request.</summary>
    public async Task ChangeReasoningAsync(string? level) => _ = await RunCommandAsync(() => CurrentSession is { } session
        ? SaveSettingsCoreAsync(session, session.Settings with { ReasoningLevel = level }) : Task.FromResult(false));

    /// <summary>Updates automatic compaction for this conversation.</summary>
    public async Task SetAutomaticCompactionAsync(bool enabled) => _ = await RunCommandAsync(() => CurrentSession is { } session
        ? SaveSettingsCoreAsync(session, session.Settings with { AutomaticCompaction = enabled }) : Task.FromResult(false));

    /// <summary>Updates the occupancy threshold and complete recent turns retained during compaction.</summary>
    public async Task SetCompactionPreferencesAsync((double Threshold, int RetainedTurns, int? OutputBudget) preferences) => _ = await RunCommandAsync(() => CurrentSession is { } session
        ? SaveSettingsCoreAsync(session, session.Settings with { CompactionThreshold = preferences.Threshold, RetainedTurns = preferences.RetainedTurns, MaxOutputTokens = preferences.OutputBudget }) : Task.FromResult(false));

    /// <summary>Edits conversation instructions; changes affect the next request.</summary>
    public async Task OpenPromptSettingsAsync() => _ = await RunCommandAsync(async () =>
    {
        if (CurrentSession is not { } session) return false;
        var capturedSettings = session.Settings;
        var capturedRevision = session.PersistenceRevision;
        var parameters = new DialogParameters
        {
            [nameof(ProviderSystemPromptDialog.ProviderName)] = CurrentProviderName,
            [nameof(ProviderSystemPromptDialog.SystemPrompt)] = session.SystemPrompt,
            [nameof(ProviderSystemPromptDialog.IsReadOnly)] = false
        };
        var result = await ShowDialogAsync<ProviderSystemPromptDialog>(
            localizer["Provider:Settings:SystemPrompt"], parameters,
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseButton = true }, _lifetime.Token);
        if (_disposed || CurrentSession != session || session.Settings != capturedSettings || session.PersistenceRevision != capturedRevision || result is not { Canceled: false }) return false;
        return await SaveSettingsCoreAsync(session, capturedSettings with { SystemPrompt = result.Data as string });
    });

    private async Task<bool> SaveSettingsCoreAsync(ChatSession session, ChatSessionSettings settings)
    {
        if (_disposed || HasHistoryConflict || HasHistoryLoadFailure || CurrentSession != session) return false;
        var result = chatFacade.UpdateSettings(session, settings);
        var error = result;
        if (result.IsFailure()) { SetError(error.Message); return false; }
        var saved = await workspace.SaveSessionAsync(session, _lifetime.Token);
        NotifyStateChanged();
        return saved;
    }
}
