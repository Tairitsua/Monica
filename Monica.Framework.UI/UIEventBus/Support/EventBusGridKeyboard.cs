using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Monica.Framework.UI.UIEventBus.Support;

/// <summary>Owns one ElementReference attachment for keyboard movement in an EventBus evidence instrument.</summary>
internal sealed class EventBusGridKeyboard : IAsyncDisposable
{
    private readonly ElementReference _grid;
    private readonly Task<IJSObjectReference> _module;
    private bool _retired;

    internal EventBusGridKeyboard(IJSRuntime js, ElementReference grid, bool wrap)
    {
        _grid = grid;
        _module = js.InvokeAsync<IJSObjectReference>("import", "./_content/Monica.Framework.UI/js/event-bus-matrix.js").AsTask();
        Ready = AttachAsync(wrap);
    }

    internal Task Ready { get; }

    private async Task AttachAsync(bool wrap)
    {
        try
        {
            var module = await _module;
            if (!_retired) await module.InvokeVoidAsync("attach", _grid, wrap);
        }
        catch (JSDisconnectedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_retired) return;
        _retired = true;
        try
        {
            await Ready;
            var module = await _module;
            try { await module.InvokeVoidAsync("detach", _grid); }
            finally { await module.DisposeAsync(); }
        }
        catch (JSDisconnectedException) { }
    }
}
