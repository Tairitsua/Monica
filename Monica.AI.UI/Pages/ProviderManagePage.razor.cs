using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Monica.AI.Configuration.Models;
using Monica.AI.UI.Shared.Support;
using Monica.AI.UI.UIProvider.Components;
using Monica.AI.UI.UIProvider.State;
using MudBlazor;

namespace Monica.AI.UI.Pages;

/// <summary>Composes host-wide settings while injected infrastructure services remain host-owned.</summary>
public partial class ProviderManagePage
{
    /// <summary>The existing provider administration route.</summary>
    public const string PAGE_URL = "/ai-providers";
    private ProviderAdministrationState State = null!;
    private AIAdministrationDialogSession _dialogs = null!;
    private readonly AIAdministrationOperationLifetime _ui = new();
    private bool _catalogExpanded;
    private bool _focusDetail;
    private bool _focusModels;
    private ElementReference _detailHeading;
    private ElementReference _modelsHeading;

    protected override async Task OnInitializedAsync()
    {
        State = new(Configuration, L);
        _dialogs = new(DialogService, action => InvokeAsync(action));
        await State.RefreshAsync();
    }
    private Task RefreshAsync() => State.RefreshAsync();
    private void ToggleCatalog() => _catalogExpanded = !_catalogExpanded;
    private void SelectProvider(string providerId)
    {
        State.SelectProvider(providerId);
        _catalogExpanded = false;
        _focusDetail = true;
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if ((!_focusDetail && !_focusModels) || State.SelectedProvider is null || _ui.IsRetired) return;
        var target = _focusModels ? _modelsHeading : _detailHeading;
        _focusDetail = _focusModels = false;
        try
        {
            await _ui.RunAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                await target.FocusAsync();
            });
        }
        catch (OperationCanceledException) when (_ui.IsRetired) { }
        catch (JSDisconnectedException) { }
    }
    private async Task EditProviderAsync(AIProviderSettings? provider)
    {
        if (State.IsBusy || State.IsRetired) return;
        var capture = State.CaptureProvider(provider);
        await _dialogs.ShowAsync<ProviderConfigurationDialog>(L[provider is null ? "Settings:AddProvider" : "Settings:EditProvider"], new()
        {
            [nameof(ProviderConfigurationDialog.Provider)] = provider,
            [nameof(ProviderConfigurationDialog.OnSave)] = (Func<ProviderConfigurationEdit, CancellationToken, Task<string?>>)(async (edit, token) =>
            {
                var error = await State.SaveProviderAsync(capture, edit, token);
                if (!State.IsRetired) await InvokeAsync(StateHasChanged);
                return error;
            }),
            [nameof(ProviderConfigurationDialog.OnDiscover)] = (Func<ProviderConfigurationEdit, CancellationToken, Task<Monica.Core.Results.Result<IReadOnlyList<AIModelConfiguration>>>>)((draft, token) => Configuration.DiscoverDraftModelsAsync(draft.Configuration, draft.ApiKey, provider?.HasApiKey == true && !draft.ClearApiKey, token))
        });
    }
    private async Task EditModelAsync(AIModelConfiguration? model)
    {
        if (State.IsBusy || State.IsRetired || State.CaptureSelectedProvider() is not { Provider: { } provider } capture) return;
        AIModelConfiguration? savedDraft = null;
        async Task<string?> Save(AIModelConfiguration draft, CancellationToken token)
        {
            var error = await State.SaveModelAsync(capture, model, draft, token);
            if (error is null && !State.IsRetired) savedDraft = draft;
            if (!State.IsRetired) await InvokeAsync(StateHasChanged);
            return error;
        }
        var result = await _dialogs.ShowAsync<ModelConfigurationDialog>(L[model is null ? "Settings:AddModel" : "Settings:EditModel"], new()
        {
            [nameof(ModelConfigurationDialog.Model)] = model,
            [nameof(ModelConfigurationDialog.ProviderType)] = provider.Configuration.ProviderType,
            [nameof(ModelConfigurationDialog.OnSave)] = (Func<AIModelConfiguration, Task<string?>>)(draft => Save(draft, CancellationToken.None)),
            [nameof(ModelConfigurationDialog.OnSaveWithCancellation)] = (Func<AIModelConfiguration, CancellationToken, Task<string?>>)Save,
            [nameof(ModelConfigurationDialog.OnDiscover)] = (Func<CancellationToken, Task<Monica.Core.Results.Result<IReadOnlyList<AIModelConfiguration>>>>)(token => Configuration.DiscoverModelsAsync(provider.Configuration.ProviderId, token))
        });
        if (!State.IsRetired && model is not null && result is { Canceled: false } && savedDraft is { } saved
            && !Same(model.ModelName, saved.ModelName))
        {
            _focusModels = true;
            await InvokeAsync(StateHasChanged);
        }
    }
    private Task ToggleProviderAsync() => !State.IsBusy && !State.IsRetired && State.CaptureSelectedProvider() is { } capture
        ? State.ToggleProviderAsync(capture) : Task.CompletedTask;
    private async Task ResetProviderAsync()
    {
        if (State.IsBusy || State.IsRetired || State.CaptureSelectedProvider() is not { Provider: { IsCodeDefined: true, HasOverride: true } provider } capture) return;
        if (await _dialogs.ConfirmAsync(L["Settings:ResetOverride"], L["Administration:ResetConfirm", DisplayName(provider.Configuration)], L["Settings:ResetOverride"]) && !State.IsBusy && !State.IsRetired) await State.ResetProviderAsync(capture);
    }
    private async Task RemoveProviderAsync()
    {
        if (State.IsBusy || State.IsRetired || State.CaptureSelectedProvider() is not { Provider: { IsCodeDefined: false } provider } capture) return;
        if (await _dialogs.ConfirmAsync(L["Settings:DeleteProvider"], L["Administration:DeleteProviderConfirm", DisplayName(provider.Configuration)], L["Common:Delete"]) && !State.IsBusy && !State.IsRetired) await State.RemoveProviderAsync(capture);
    }
    private async Task DeleteModelAsync(AIModelConfiguration model)
    {
        if (State.IsBusy || State.IsRetired || State.CaptureSelectedProvider() is not { } capture) return;
        if (await _dialogs.ConfirmAsync(L["Administration:DeleteModel"], L["Administration:DeleteModelConfirm", model.ModelName], L["Common:Delete"]) && !State.IsBusy && !State.IsRetired) await State.DeleteModelAsync(capture, model);
    }
    private Task SetDefaultModelAsync(AIModelConfiguration model) => !State.IsBusy && !State.IsRetired && State.CaptureSelectedProvider() is { } capture
        ? State.SetDefaultModelAsync(capture, model) : Task.CompletedTask;
    private async Task ImportModelAsync(AIModelConfiguration model)
    {
        if (State.IsBusy || State.IsRetired || State.CaptureSelectedProvider() is not { Provider: { } provider } capture
            || provider.Configuration.Models.Any(existing => Same(existing.ModelName, model.ModelName))) return;
        var diagnostic = await State.ImportModelAsync(capture, model);
        if (!State.IsRetired && diagnostic is null && Same(State.SelectedProviderId, provider.Configuration.ProviderId))
        {
            // A successful save retires the discovered list that contained the initiating button.
            _focusModels = true;
            await InvokeAsync(StateHasChanged);
        }
    }
    private Task DiscoverAsync() => State.IsBusy || State.IsRetired || State.SelectedProvider?.Configuration.Enabled != true
        ? Task.CompletedTask : State.DiscoverAsync();
    private Task TestProviderAsync() => !State.IsBusy && !State.IsRetired && State.CaptureSelectedProvider() is { } capture
        ? State.TestProviderAsync(capture) : Task.CompletedTask;
    private Task TestModelAsync(AIModelConfiguration model) => !State.IsBusy && !State.IsRetired && State.CaptureSelectedProvider() is { } capture
        ? State.TestModelAsync(capture, model) : Task.CompletedTask;
    private static string DisplayName(AIProviderConfiguration provider) => string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.ProviderId : provider.DisplayName;
    private string Source(AIProviderSettings provider) => L[provider.IsCodeDefined ? provider.HasOverride ? "Settings:CodeOverride" : "Settings:CodeDefaults" : "Settings:Persisted"];
    private static bool Same(string? left, string? right) => left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    /// <summary>Retires dialogs and drains component-owned facade work without disposing borrowed services.</summary>
    public async ValueTask DisposeAsync()
    {
        if (State is null || _dialogs is null) return;
        await Task.WhenAll(_ui.RetireAsync().AsTask(), _dialogs.DisposeAsync().AsTask(), State.DisposeAsync().AsTask());
    }
}
