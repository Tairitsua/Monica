using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Monica.UI.Shared.Components.Dialogs;

/// <summary>A modal evidence panel using the native dialog provider, focus handling, and dismissal lifecycle.</summary>
/// <remarks>
/// Requires the normal Monica UI theme assets and MudDialogProvider. Inline callers bind Open/OpenChanged;
/// the component owns and retires that provider portal. Inside a service-opened dialog, Open is ignored and
/// the calling dialog session owns its reference. Callers retain data, navigation, and mutation lifetimes.
/// </remarks>
public partial class MoSideSheet : IAsyncDisposable
{
    /// <summary>Gets or sets the heading used when TitleContent is omitted.</summary>
    [Parameter] public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets localized heading content, excluding close controls and outer padding.</summary>
    [Parameter] public RenderFragment? TitleContent { get; set; }

    /// <summary>Gets or sets stationary content below the heading, such as a toolbar.</summary>
    [Parameter] public RenderFragment? HeaderContent { get; set; }

    /// <summary>Gets or sets evidence inside the single scrolling body. The component owns outer padding.</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>Gets or sets native MudTabPanel children as an alternative to ChildContent.</summary>
    /// <remarks>The component owns MudTabs and scrolls its panels while preserving native tab associations.</remarks>
    [Parameter] public RenderFragment? TabPanels { get; set; }

    /// <summary>Gets or sets the active native tab index. Defaults to zero; used only with TabPanels.</summary>
    [Parameter] public int ActiveTabIndex { get; set; }

    /// <summary>Notifies the owner of a native tab selection.</summary>
    [Parameter] public EventCallback<int> ActiveTabIndexChanged { get; set; }

    /// <summary>Gets or sets optional stationary footer actions.</summary>
    [Parameter] public RenderFragment? FooterContent { get; set; }

    /// <summary>Gets or sets inline visibility. Defaults to false; ignored inside a service-opened dialog.</summary>
    [Parameter] public bool Open { get; set; }

    /// <summary>Notifies the inline owner of native dismissal. The owner must accept false before reopening.</summary>
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Gets or sets the desired CSS width, capped at the viewport. Defaults to 480px.</summary>
    [Parameter] public string Width { get; set; } = "480px";

    /// <summary>Gets or sets whether Close, Escape, and backdrop dismissal are enabled. Defaults to true.</summary>
    /// <remarks>Disabling dismissal does not prevent route retirement or disposal of owned business work.</remarks>
    [Parameter] public bool Dismissible { get; set; } = true;

    /// <summary>Gets or sets a contextual Close label; omission uses the shared localized Close text.</summary>
    [Parameter] public string? CloseLabel { get; set; }

    [CascadingParameter] private IMudDialogInstance? DialogInstance { get; set; }
    [CascadingParameter(Name = "IsNested")] private bool IsNested { get; set; }

    private static readonly DialogOptions Options = new()
    {
        CloseButton = false,
        CloseOnEscapeKey = true,
        BackdropClick = true,
        FullWidth = true,
        MaxWidth = MaxWidth.False,
        Position = DialogPosition.CenterRight,
        BackgroundClass = "mo-side-sheet-backdrop"
    };
    private readonly string _headingId = $"mo-side-sheet-heading-{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private MudDialog? _dialog;
    private IDialogReference? _reference;
    private Task? _retirement;
    private bool _retired, _dismissed, _requestedOpen;
    private bool IsHosted => DialogInstance is not null && !IsNested;
    private DialogOptions CurrentOptions => Dismissible ? Options : Options with { CloseOnEscapeKey = false, BackdropClick = false };

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (_requestedOpen == Open) return;
        _requestedOpen = Open;
        _dismissed = false;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_retired || IsHosted) return;
        await _lifecycle.WaitAsync();
        try
        {
            if (_retired) return;
            if (Open && !_dismissed)
            {
                if (_reference is null || _reference.Result.IsCompleted)
                    _reference = await _dialog!.ShowAsync();
                if (!_retired && Open) return;
            }
            if (_reference is not null)
            {
                await _dialog!.CloseAsync();
                _reference = null;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task OnVisibilityChangedAsync(bool visible)
    {
        if (visible || _retired || !Open || IsHosted) return;
        _dismissed = true;
        await OpenChanged.InvokeAsync(false);
    }

    private async Task CloseAsync()
    {
        if (_retired || !Dismissible) return;
        if (IsHosted) DialogInstance!.Cancel();
        else if (_dialog is not null) await _dialog.CloseAsync();
    }

    /// <summary>Drains native opening and closes the owned inline portal, including a late-produced reference.</summary>
    public ValueTask DisposeAsync() => new(_retirement ??= RetireAsync());

    private async Task RetireAsync()
    {
        _retired = true;
        if (IsHosted) return;
        await _lifecycle.WaitAsync();
        try
        {
            if (_dialog is not null) await _dialog.CloseAsync();
            _reference = null;
        }
        finally { _lifecycle.Release(); }
    }
}
