using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Monica.AI.Chat.Models;
using Monica.AI.Configuration.Models;
using Monica.AI.Providers;
using Monica.Core.Results;
using Monica.AI.UI.Shared.Support;
using MudBlazor;

namespace Monica.AI.UI.UIProvider.Components;

/// <summary>Edits one provider-scoped model draft, preserving unknown capabilities and explicit protocol mappings.</summary>
public partial class ModelConfigurationDialog
{
    /// <summary>The containing dialog; a successful save closes it.</summary>
    [CascadingParameter] public IMudDialogInstance Dialog { get; set; } = null!;
    /// <summary>Initial saved or discovered values; null creates an empty draft.</summary>
    [Parameter] public AIModelConfiguration? Model { get; set; }
    /// <summary>Indicates that save changes only the containing unsaved provider draft.</summary>
    [Parameter] public bool IsProviderDraft { get; set; }
    /// <summary>Controls provider-specific reasoning options.</summary>
    [Parameter] public EAIProviderType ProviderType { get; set; }
    /// <summary>Saves the draft; a safe diagnostic keeps the dialog open on failure.</summary>
    [Parameter, EditorRequired] public required Func<AIModelConfiguration, Task<string?>> OnSave { get; set; }
    /// <summary>
    /// Optional cancellation-aware save callback, preferred over <see cref="OnSave"/> when supplied. A diagnostic retains the draft.
    /// Dialog retirement requests cancellation and drains the callback; cancellation cannot undo a completed durable write.
    /// </summary>
    [Parameter] public Func<AIModelConfiguration, CancellationToken, Task<string?>>? OnSaveWithCancellation { get; set; }
    /// <summary>Optional cancellable discovery using the owning provider's current connection. Does not persist edits.</summary>
    [Parameter] public Func<CancellationToken, Task<Result<IReadOnlyList<AIModelConfiguration>>>>? OnDiscover { get; set; }

    private readonly AIAdministrationOperationLifetime _lifetime = new();
    private bool _saving, _fetching;
    private AIModelConfiguration _initialModel = new() { ModelName = string.Empty };
    private MudTextField<string>? _nameField, _contextField, _outputField;
    private string _name = string.Empty, _contextText = string.Empty, _outputText = string.Empty;
    private string? _display, _description, _defaultReasoning, _error, _metadataMessage;
    private AIModelKind _kind;
    private int? _dimensions;
    private bool? _images, _documents, _tools, _reasoning;
    private List<ReasoningForm> _levels = [];
    private bool Busy => _saving || _fetching;
    private bool InvalidContext => !TokenQuantity.TryParse(_contextText, out _);
    private bool InvalidOutput => !TokenQuantity.TryParse(_outputText, out _);
    private IReadOnlyList<string> SelectedLevelIds => _levels
        .Where(level => !string.IsNullOrWhiteSpace(level.Id))
        .Select(level => level.Id.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    protected override void OnInitialized()
    {
        if (Model is { } model) _initialModel = model with { ReasoningLevels = model.ReasoningLevels.ToArray() };
        Reset();
    }

    private void Reset()
    {
        if (Busy || _lifetime.IsRetired) return;
        Load(_initialModel);
        _error = null;
        _metadataMessage = null;
    }

    private void Load(AIModelConfiguration model)
    {
        _name = model.ModelName; _display = model.DisplayName; _description = model.Description; _kind = model.Kind;
        _contextText = TokenQuantity.Format(model.ContextWindow); _outputText = TokenQuantity.Format(model.MaxOutputTokens);
        _dimensions = model.EmbeddingDimensions;
        _images = model.SupportsImage; _documents = model.SupportsDocuments; _tools = model.SupportsTools; _reasoning = model.SupportsReasoning;
        _defaultReasoning = model.DefaultReasoningLevel;
        _levels = model.ReasoningLevels.Select(ReasoningForm.From).ToList();
    }

    private string TokenPreview(string text, bool useDefault)
    {
        if (!TokenQuantity.TryParse(text, out var tokens)) return string.Empty;
        return tokens is { } count ? L["Settings:ExactTokens", count]
            : useDefault ? L["Settings:DefaultContext", ChatContextCapacity.DefaultTokens] : L["Settings:UnknownNumber"];
    }

    private void SetReasoning(bool? value)
    {
        if (Busy || _lifetime.IsRetired) return;
        _reasoning = value;
        if (value == false) { _levels.Clear(); _defaultReasoning = null; }
    }

    private void SetLevels(IReadOnlyList<string> ids)
    {
        if (Busy || _lifetime.IsRetired) return;
        // Retain custom mappings when the supported-level picker changes its selection.
        _levels = ids.Select(id => _levels.FirstOrDefault(level => level.Id.Trim().Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? new ReasoningForm { Id = id, ProviderValue = id }).ToList();
        if (_levels.Count > 0) _reasoning = true;
        if (!_levels.Any(level => level.Id.Trim().Equals(_defaultReasoning, StringComparison.OrdinalIgnoreCase))) _defaultReasoning = null;
    }

    private void AddLevel() { if (Busy || _lifetime.IsRetired) return; _levels.Add(new ReasoningForm()); _reasoning = true; }
    private void RemoveLevel(ReasoningForm level)
    {
        if (Busy || _lifetime.IsRetired) return;
        _levels.Remove(level);
        if (string.Equals(_defaultReasoning, level.Id, StringComparison.OrdinalIgnoreCase)) _defaultReasoning = null;
    }

    private AIModelConfiguration Build(int? context, int? output) => _initialModel with
    {
        ModelName = _name.Trim(), DisplayName = string.IsNullOrWhiteSpace(_display) ? null : _display.Trim(), Description = _description, Kind = _kind,
        ContextWindow = context, MaxOutputTokens = output, EmbeddingDimensions = _kind == AIModelKind.Embedding ? _dimensions : null,
        SupportsImage = _kind == AIModelKind.Chat ? _images : null, SupportsDocuments = _kind == AIModelKind.Chat ? _documents : null,
        SupportsTools = _kind == AIModelKind.Chat ? _tools : null, SupportsReasoning = _kind == AIModelKind.Chat ? _reasoning : null,
        ReasoningLevels = _kind == AIModelKind.Chat ? _levels.Select(level => new AIReasoningLevel
        {
            Id = level.Id.Trim(), DisplayName = level.DisplayName,
            ProviderValue = string.IsNullOrWhiteSpace(level.ProviderValue) ? null : level.ProviderValue.Trim(),
            BudgetTokens = ProviderType == EAIProviderType.Anthropic ? level.Budget : null
        }).ToArray() : [],
        DefaultReasoningLevel = _kind == AIModelKind.Chat
            ? _levels.FirstOrDefault(level => level.Id.Trim().Equals(_defaultReasoning, StringComparison.OrdinalIgnoreCase))?.Id.Trim()
            : null
    };

    private Task FetchMetadataAsync() => _lifetime.RunAsync(FetchMetadataCoreAsync);
    private async Task FetchMetadataCoreAsync(CancellationToken cancellationToken)
    {
        if (Busy || _lifetime.IsRetired || OnDiscover is null || string.IsNullOrWhiteSpace(_name)) return;
        if (!TokenQuantity.TryParse(_contextText, out var context))
        { _error = L["Settings:InvalidTokenQuantity"]; await FocusFieldAsync(_contextField); return; }
        if (!TokenQuantity.TryParse(_outputText, out var output))
        { _error = L["Settings:InvalidTokenQuantity"]; await FocusFieldAsync(_outputField); return; }
        var draft = Build(context, output);
        _fetching = true; _error = null; _metadataMessage = null;
        try
        {
            var result = await OnDiscover(cancellationToken);
            if (_lifetime.IsRetired) return;
            if (!DraftMatches(draft)) { _metadataMessage = L["Settings:ModelDraftChanged"]; return; }
            var error = result.WithoutData();
            var models = result.Data;
            if (result.IsFailure() || models is null) { _error = error.Message ?? L["Error:Generic"]; return; }
            var evidence = models.FirstOrDefault(model => model.ModelName.Equals(draft.ModelName, StringComparison.OrdinalIgnoreCase));
            if (evidence is null) { _metadataMessage = L["Settings:MetadataNotFound"]; return; }
            Load(draft.WithDiscoveredMetadata(evidence));
            _metadataMessage = L["Settings:MetadataApplied"];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { if (!_lifetime.IsRetired) _error = L["Error:Generic"]; }
        finally { if (!_lifetime.IsRetired) _fetching = false; }
    }

    private bool DraftMatches(AIModelConfiguration draft)
    {
        if (!TokenQuantity.TryParse(_contextText, out var context) || !TokenQuantity.TryParse(_outputText, out var output)) return false;
        var current = Build(context, output);
        return current.ReasoningLevels.SequenceEqual(draft.ReasoningLevels)
            && (current with { ReasoningLevels = draft.ReasoningLevels }) == draft;
    }

    private Task SaveAsync() => _lifetime.RunAsync(SaveCoreAsync);
    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        if (Busy || _lifetime.IsRetired) return;
        if (string.IsNullOrWhiteSpace(_name)) { _error = L["Settings:IdRequired"]; await FocusFieldAsync(_nameField); return; }
        if (!TokenQuantity.TryParse(_contextText, out var context))
        { _error = L["Settings:InvalidTokenQuantity"]; await FocusFieldAsync(_contextField); return; }
        if (!TokenQuantity.TryParse(_outputText, out var output))
        { _error = L["Settings:InvalidTokenQuantity"]; await FocusFieldAsync(_outputField); return; }
        var capacity = _kind == AIModelKind.Chat ? ChatContextCapacity.Resolve(null, context).Tokens : context;
        if ((_kind == AIModelKind.Embedding && _dimensions is <= 0) || (capacity is { } limit && output >= limit))
        { _error = L["Settings:InvalidModelLimits"]; return; }
        if (_kind == AIModelKind.Chat)
        {
            if (_levels.Any(level => string.IsNullOrWhiteSpace(level.Id))
                || _levels.Select(level => level.Id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _levels.Count)
            { _error = L["Settings:InvalidLevels"]; return; }
            var invalidBudget = ProviderType == EAIProviderType.Anthropic && _levels.Any(level => level.Budget is { } budget
                && (budget < 1024 || output is { } maximum && budget >= maximum || string.Equals(level.ProviderValue, "none", StringComparison.OrdinalIgnoreCase)));
            if ((_reasoning == false && _levels.Count > 0) || invalidBudget)
            { _error = L["Settings:InvalidReasoningConfiguration"]; return; }
        }
        _saving = true;
        try
        {
            var draft = Build(context, output);
            var error = OnSaveWithCancellation is { } save
                ? await save(draft, cancellationToken)
                : await OnSave(draft);
            if (_lifetime.IsRetired) return;
            _error = error;
            if (error is null) Dialog.Close(DialogResult.Ok(true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { if (!_lifetime.IsRetired) _error = L["Error:Generic"]; }
        finally { if (!_lifetime.IsRetired) _saving = false; }
    }

    private async Task FocusFieldAsync(MudTextField<string>? field)
    {
        if (_lifetime.IsRetired || field is null) return;
        try { await field.FocusAsync(); }
        catch (JSDisconnectedException) { }
    }

    private async Task Cancel()
    {
        var retirement = DisposeAsync();
        Dialog.Cancel();
        await retirement;
    }

    /// <summary>Cancels and drains discovery and save callbacks after the dialog closes.</summary>
    public ValueTask DisposeAsync() => _lifetime.RetireAsync();

    private sealed class ReasoningForm
    {
        public string Id { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string? ProviderValue { get; set; }
        public int? Budget { get; set; }
        public static ReasoningForm From(AIReasoningLevel level) => new() { Id = level.Id, DisplayName = level.DisplayName, ProviderValue = level.ProviderValue, Budget = level.BudgetTokens };
    }
}
