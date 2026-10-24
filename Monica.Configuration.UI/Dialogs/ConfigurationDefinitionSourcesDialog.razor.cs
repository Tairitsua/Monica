using Microsoft.AspNetCore.Components;
using Monica.Configuration.Models;
using Monica.Configuration.UI.Support;
using Monica.Core.Results;
using MudBlazor;

namespace Monica.Configuration.UI.Dialogs;

/// <summary>Inspects actual provider contribution counts for one locally scanned definition.</summary>
/// <remarks>
/// Initial definition identity is frozen for this native dialog's lifetime. The dialog owns its nested file
/// producer, native result and acquired async component through idempotent retirement. Custom owners must
/// await <see cref="DisposeAsync"/>; injected facade, dialog service and localizer remain borrowed.
/// </remarks>
public partial class ConfigurationDefinitionSourcesDialog
{
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;

    /// <summary>Gets or sets the initial definition whose actual local source contributions are inspected.</summary>
    /// <remarks>
    /// The exact definition key and display name are captured during initialization. Changing this parameter
    /// does not retarget an open inspector; create a new native dialog for another definition.
    /// </remarks>
    [Parameter, EditorRequired] public ConfigurationDefinition Definition { get; set; } = null!;

    private readonly ConfigurationOperationLifetime _operations = new();
    private readonly List<ConfigurationDefinitionSourceContribution> _contributions = [];
    private string _definitionKey = string.Empty;
    private string _definitionDisplayName = string.Empty;
    private ConfigurationKind _definitionKind;
    private string? _error;
    private string? _fileError;
    private string? _fileOpenerSourceKey;
    private bool _loading = true;
    private bool _fileDialogOpen;

    protected override async Task OnInitializedAsync()
    {
        _definitionKey = Definition.DefinitionKey;
        _definitionDisplayName = Definition.DisplayName;
        _definitionKind = Definition.Kind;
        try
        {
            await _operations.RunAsync(async _ =>
            {
                // The current facade computes synchronously and returns an already completed task.
                var result = await Facade.GetDefinitionSourceContributionsAsync(_definitionKey);
                _operations.TryApply(() =>
                {
                    if (result.IsFailed(out var error, out var contributions)) _error = error.Message;
                    else _contributions.AddRange(contributions);
                    _loading = false;
                });
            });
        }
        catch (OperationCanceledException) when (_operations.IsRetired) { }
    }

    private bool FileButtonDisabled(ConfigurationSourceDescriptor source) => _fileDialogOpen
        ? !string.Equals(_fileOpenerSourceKey, source.SourceKey, StringComparison.Ordinal)
        : _operations.IsRetired;

    private async Task ViewFileAsync(ConfigurationSourceDescriptor source)
    {
        if (source.Kind != ConfigurationSourceKind.JsonFile) return;
        var sourceKey = source.SourceKey;
        var title = L["Sources:File:Title", source.DisplayName].Value;
        var admitted = false;
        if (!_operations.TryApply(() =>
            {
                if (_fileDialogOpen) return;
                _fileDialogOpen = admitted = true;
                _fileOpenerSourceKey = sourceKey;
                _fileError = null;
            }) || !admitted) return;

        try
        {
            var parameters = new DialogParameters<ConfigurationSourceFileDialog>();
            parameters.Add(dialog => dialog.SourceKey, sourceKey);
            await _operations.ShowDialogAsync(
                () => DialogService.ShowAsync<ConfigurationSourceFileDialog>(title, parameters, new DialogOptions
                {
                    MaxWidth = MaxWidth.ExtraLarge,
                    FullWidth = true,
                    CloseButton = true,
                    CloseOnEscapeKey = true
                }),
                action => InvokeAsync(action));
        }
        catch (OperationCanceledException) when (_operations.IsRetired) { }
        catch (Exception exception) when (!_operations.IsRetired)
        {
            _operations.TryApply(() => _fileError = $"{L["Common:Errors:LoadFailed"]}: {exception.Message}");
        }
        finally
        {
            _operations.TryApply(() =>
            {
                _fileDialogOpen = false;
                _fileOpenerSourceKey = null;
            });
        }
    }

    private static string SourcePath(ConfigurationSourceDescriptor source) =>
        source.PhysicalPath ?? source.SourcePath ?? source.ProviderType;

    private async Task CloseAsync()
    {
        if (_operations.IsRetired) return;
        // Begin closing owned children before joining work which awaits their actual native results.
        var retirement = DisposeAsync().AsTask();
        var failures = new List<Exception>();
        try { MudDialog.Close(); }
        catch (Exception exception) { failures.Add(exception); }
        try { await retirement; }
        catch (Exception exception) { failures.Add(exception); }
        if (failures.Count != 0) throw new AggregateException("Configuration source inspector closure failed.", failures);
    }

    /// <summary>Stops new file admission and joins actual contribution work and nested native/file retirement.</summary>
    /// <remarks>
    /// Repeated calls share completion. Retirement cannot cancel the tokenless source-file read or dispose
    /// borrowed services; it closes the owned native child and joins its actual read and component drainage.
    /// </remarks>
    public ValueTask DisposeAsync() => _operations.RetireAsync();
}
