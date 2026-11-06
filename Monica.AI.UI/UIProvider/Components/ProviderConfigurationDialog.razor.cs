using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Monica.AI.Chat.Models;
using Monica.AI.Configuration.Models;
using Monica.AI.Providers;
using Monica.Core.Results;
using Monica.AI.UI.Shared.Support;
using MudBlazor;

namespace Monica.AI.UI.UIProvider.Components;

/// <summary>Owns an unsaved provider draft, cancellable model discovery, and local model edits.</summary>
public partial class ProviderConfigurationDialog
{
    /// <summary>The containing MudBlazor dialog.</summary>
    [CascadingParameter] public IMudDialogInstance Dialog { get; set; } = null!;
    /// <summary>Saved values to edit, or null for a new provider.</summary>
    [Parameter] public AIProviderSettings? Provider { get; set; }
    /// <summary>Persists the complete draft; return a safe diagnostic to keep the dialog open on failure.</summary>
    [Parameter, EditorRequired] public required Func<ProviderConfigurationEdit, CancellationToken, Task<string?>> OnSave { get; set; }
    /// <summary>Fetches endpoint model metadata without saving the supplied draft.</summary>
    [Parameter, EditorRequired] public required Func<ProviderConfigurationEdit, CancellationToken, Task<Result<IReadOnlyList<AIModelConfiguration>>>> OnDiscover { get; set; }
    private bool _saving, _editingModel, _restoreModelsFocus;
    private ElementReference _modelsHeading;
    private AIProviderConfiguration? _initialConfiguration;
    private MudTextField<string>? _idField, _urlField;
    private readonly AIAdministrationOperationLifetime _lifetime = new();
    private AIAdministrationDialogSession _modelDialogs = null!;
    private CancellationTokenSource? _discoveryCancellation;
    private bool _discovering, _hasDiscovered;
    private string? _discoveryError, _defaultModel;
    private string _query = string.Empty;
    private List<AIModelConfiguration> _models = [];
    private IReadOnlyList<AIModelConfiguration> _discovered = [];
    private readonly HashSet<string> _selectedModels = new(StringComparer.OrdinalIgnoreCase);
    private (string Id, string? Url, string? Key, EAIProviderType Type, OpenAIProviderApiMode Mode, OpenAIProtocolProfile Profile, int Timeout, bool Clear)? _discoveryConnection;
    private (string Id, string? Url, string? Key, EAIProviderType Type, OpenAIProviderApiMode Mode, OpenAIProtocolProfile Profile, int Timeout, bool Clear) CurrentConnection =>
        (_id.Trim(), string.IsNullOrWhiteSpace(_url) ? null : _url.Trim(), _key, _type, _apiMode, _profile, _timeout, _clearKey);
    private bool DiscoveryMatches => _discoveryConnection == CurrentConnection;
    private ICollection<AIModelConfiguration> FilteredDiscovery => _discovered.Where(model => model.ModelName.Contains(_query, StringComparison.OrdinalIgnoreCase)).ToArray();
    private string _id = string.Empty;
    private string? _name, _url, _key, _prompt, _error;
    private EAIProviderType _type;
    private OpenAIProviderApiMode _apiMode = OpenAIProviderApiMode.Chat;
    private OpenAIProtocolProfile _profile;
    private bool _enabled = true, _default, _clearKey;
    private int _timeout = 120;
    protected override void OnInitialized()
    {
        _modelDialogs = new(DialogService, action => InvokeAsync(action));
        _initialConfiguration = Provider?.Configuration;
        if (_initialConfiguration is not { } value) return;
        _id = value.ProviderId; _name = value.DisplayName; _url = value.BaseUrl; _prompt = value.SystemPrompt;
        _type = value.ProviderType; _apiMode = value.OpenAIApiMode; _profile = value.OpenAIProtocolProfile; _enabled = value.Enabled;
        _default = value.IsDefault; _timeout = value.TimeoutSeconds;
        _models = value.Models.ToList(); _defaultModel = value.DefaultModel;
    }
    private async Task Cancel()
    {
        var retirement = DisposeAsync();
        Dialog.Cancel();
        await retirement;
    }
    private Task SaveAsync() => _lifetime.RunAsync(SaveCoreAsync);
    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        if (_saving || _discovering || _editingModel || _lifetime.IsRetired) return;
        if (string.IsNullOrWhiteSpace(_id)) { _error = L["Settings:IdRequired"]; await FocusFieldAsync(_idField); return; }
        if (!string.IsNullOrWhiteSpace(_url) && (!Uri.TryCreate(_url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")))
        { _error = L["Settings:InvalidUrl"]; await FocusFieldAsync(_urlField); return; }
        var configuration = BuildConfiguration();
        _saving = true;
        try
        {
            var error = await OnSave(new ProviderConfigurationEdit(configuration, _key, _clearKey), cancellationToken);
            if (_lifetime.IsRetired) return;
            _error = error;
            if (error is null) { _key = null; Dialog.Close(DialogResult.Ok(true)); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { if (!_lifetime.IsRetired) _error = L["Error:Generic"]; }
        finally { if (!_lifetime.IsRetired) _saving = false; }
    }
    private AIProviderConfiguration BuildConfiguration() => (_initialConfiguration ?? new AIProviderConfiguration { ProviderId = _id.Trim() }) with
    {
        DisplayName = _name, BaseUrl = string.IsNullOrWhiteSpace(_url) ? null : _url.Trim(),
        ProviderType = _type, OpenAIApiMode = _apiMode, OpenAIProtocolProfile = _profile, SystemPrompt = _prompt, Enabled = _enabled,
        IsDefault = _default, TimeoutSeconds = _timeout, Models = _models.ToArray(), DefaultModel = _defaultModel
    };

    private Task FetchModelsAsync() => _lifetime.RunAsync(FetchModelsCoreAsync);
    private async Task FetchModelsCoreAsync(CancellationToken cancellationToken)
    {
        if (_discovering || _saving || _editingModel || _lifetime.IsRetired) return;
        _discovering = true; _discoveryError = null;
        var connection = CurrentConnection;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _discoveryCancellation = cancellation;
        try
        {
            var result = await OnDiscover(new ProviderConfigurationEdit(BuildConfiguration(), _key, _clearKey), cancellation.Token);
            if (_lifetime.IsRetired || cancellation.IsCancellationRequested) return;
            if (connection != CurrentConnection)
            { _discoveryError = L["Settings:DraftConnectionChanged"]; return; }
            var error = result.WithoutData();
            var models = result.Data;
            if (result.IsFailure() || models is null) { _discoveryError = error.Message ?? L["Error:Generic"]; return; }
            _discovered = models; _hasDiscovered = true; _discoveryConnection = connection; _selectedModels.Clear();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { if (!_lifetime.IsRetired) _discoveryError = L["Error:Generic"]; }
        finally { _discoveryCancellation = null; if (!_lifetime.IsRetired) _discovering = false; }
    }

    private async Task FocusFieldAsync(MudTextField<string>? field)
    {
        if (_lifetime.IsRetired || field is null) return;
        try { await field.FocusAsync(); }
        catch (JSDisconnectedException) { }
    }

    private void CancelDiscovery() => _discoveryCancellation?.Cancel();
    private void SelectModel(string id, bool selected) { if (_saving || _discovering || _editingModel || _lifetime.IsRetired || !DiscoveryMatches) return; if (selected) _selectedModels.Add(id); else _selectedModels.Remove(id); }
    private void SelectVisible() { if (_saving || _discovering || _editingModel || _lifetime.IsRetired || !DiscoveryMatches) return; foreach (var model in FilteredDiscovery) _selectedModels.Add(model.ModelName); }
    private void ImportSelected()
    {
        if (_selectedModels.Count == 0 || _saving || _discovering || _editingModel || _lifetime.IsRetired) return;
        if (!DiscoveryMatches) { _discoveryError = L["Settings:DraftConnectionChanged"]; return; }
        foreach (var model in _discovered.Where(model => _selectedModels.Contains(model.ModelName)))
        {
            var index = _models.FindIndex(existing => string.Equals(existing.ModelName, model.ModelName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) _models.Add(model); else _models[index] = _models[index].WithDiscoveredMetadata(model);
        }
        _selectedModels.Clear();
        _defaultModel ??= _models.FirstOrDefault(model => model.Kind == AIModelKind.Chat)?.ModelName;
    }
    private void RemoveModel(AIModelConfiguration model)
    {
        if (_saving || _discovering || _editingModel || _lifetime.IsRetired) return;
        _models.Remove(model);
        if (string.Equals(_defaultModel, model.ModelName, StringComparison.OrdinalIgnoreCase)) _defaultModel = null;
    }
    private Task EditModelAsync(AIModelConfiguration? model) => _lifetime.RunAsync(async cancellationToken =>
    {
        if (_saving || _discovering || _editingModel || _lifetime.IsRetired) return;
        _editingModel = true;
        var connection = CurrentConnection;
        var draft = new ProviderConfigurationEdit(BuildConfiguration(), _key, _clearKey);
        try
        {
            var result = await _modelDialogs.ShowAsync<ModelConfigurationDialog>(model is null ? L["Settings:AddModel"] : L["Settings:EditModel"],
                new DialogParameters
                {
                    [nameof(ModelConfigurationDialog.Model)] = model,
                    [nameof(ModelConfigurationDialog.ProviderType)] = _type,
                    [nameof(ModelConfigurationDialog.IsProviderDraft)] = true,
                    [nameof(ModelConfigurationDialog.OnDiscover)] = (Func<CancellationToken, Task<Result<IReadOnlyList<AIModelConfiguration>>>>)(ct =>
                        _lifetime.RunAsync<Result<IReadOnlyList<AIModelConfiguration>>>(async ownerToken =>
                        {
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ownerToken);
                            var result = await OnDiscover(draft, linked.Token);
                            linked.Token.ThrowIfCancellationRequested();
                    if (connection != CurrentConnection) return Result.Failure<IReadOnlyList<AIModelConfiguration>>(L["Settings:DraftConnectionChanged"].Value);
                            return result;
                        })),
                    [nameof(ModelConfigurationDialog.OnSave)] = (Func<AIModelConfiguration, Task<string?>>)(saved =>
                        _lifetime.RunAsync(_ => SaveModelDraftAsync(model, saved))),
                    [nameof(ModelConfigurationDialog.OnSaveWithCancellation)] = (Func<AIModelConfiguration, CancellationToken, Task<string?>>)((saved, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        return _lifetime.RunAsync(_ => SaveModelDraftAsync(model, saved));
                    })
                }, new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium, CloseButton = true, CloseOnEscapeKey = true });
            if (!_lifetime.IsRetired && result is { Canceled: false } && model is not null
                && !_models.Any(current => string.Equals(current.ModelName, model.ModelName, StringComparison.OrdinalIgnoreCase)))
                _restoreModelsFocus = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { if (!_lifetime.IsRetired) _error = L["Error:Generic"]; }
        finally
        {
            if (!_lifetime.IsRetired) _editingModel = false;
        }
    });

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_restoreModelsFocus || _lifetime.IsRetired) return;
        _restoreModelsFocus = false;
        await _lifetime.RunAsync(async _ =>
        {
            if (_lifetime.IsRetired) return;
            try { await _modelsHeading.FocusAsync(); }
            catch (JSDisconnectedException) { }
        });
    }

    private async Task<string?> SaveModelDraftAsync(AIModelConfiguration? original, AIModelConfiguration saved)
    {
        if (_lifetime.IsRetired) return L["Error:Generic"];
        if (_models.Any(existing => !ReferenceEquals(existing, original) && string.Equals(existing.ModelName, saved.ModelName, StringComparison.OrdinalIgnoreCase)))
            return L["Settings:DuplicateModelId"];
        var index = original is null ? -1 : _models.IndexOf(original);
        if (original is not null && index < 0) return L["Error:Generic"];
        if (index < 0) _models.Add(saved); else _models[index] = saved;
        if (string.Equals(_defaultModel, original?.ModelName, StringComparison.OrdinalIgnoreCase) && original is not null)
            _defaultModel = saved.Kind == AIModelKind.Chat ? saved.ModelName : null;
        await InvokeAsync(() => { if (!_lifetime.IsRetired) StateHasChanged(); });
        return null;
    }
    private string ModelSummary(AIModelConfiguration model)
    {
        var capacity = model.ContextWindow is { } tokens ? $"{tokens:N0}" : model.Kind == AIModelKind.Chat
            ? L["Settings:DefaultContext", ChatContextCapacity.DefaultTokens] : L["Settings:Unknown"];
        var capabilities = new List<string>();
        if (model.SupportsImage == true) capabilities.Add(L["Settings:ImageInput"]);
        if (model.SupportsDocuments == true) capabilities.Add(L["Settings:NativeDocuments"]);
        if (model.SupportsTools == true) capabilities.Add(L["Settings:FunctionTools"]);
        if (model.SupportsReasoning == true) capabilities.Add(L["Workbench:Reasoning"]);
        return capacity + " · " + (capabilities.Count > 0 ? string.Join(" · ", capabilities)
            : L[model.SupportsImage == false && model.SupportsDocuments == false && model.SupportsTools == false && model.SupportsReasoning == false
                ? "Settings:TextOnly" : "Settings:CapabilitiesUnknown"]);
    }
    /// <summary>Cancels and drains draft work, closes the owned nested editor, and clears transient credentials.</summary>
    public async ValueTask DisposeAsync()
    {
        var retirement = _lifetime.RetireAsync(() =>
        {
            _key = null;
            _discoveryConnection = null;
            return ValueTask.CompletedTask;
        });
        try { await _modelDialogs.DisposeAsync(); }
        finally { await retirement; }
    }
}
