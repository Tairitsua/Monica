using Monica.DevOps.FileOps.Facades;

namespace Monica.DevOps.FileOps.State;

/// <summary>Owns one Files workbench session inside a Blazor circuit.</summary>
/// <remarks>Sibling routes borrow the same controller and drafts. A confirmed departure ends the visit.
/// Neither draft contents nor checked batch paths are written to browser history or storage.</remarks>
public sealed class FileOpsNavigationState : IAsyncDisposable
{
    private FileOpsWorkspaceController? _controller;
    internal event Func<Task>? Changed;
    internal FileOpsPolicyDraft? PolicyDraft { get; set; }
    internal FileOpsViewQuery FilesQuery { get; set; } = new();

    internal FileOpsWorkspaceController Borrow(FileOpsFacade facade) =>
        _controller ??= new FileOpsWorkspaceController(facade, NotifyAsync);

    internal async ValueTask EndVisitAsync(FileOpsWorkspaceController controller)
    {
        if (!ReferenceEquals(controller, _controller)) return;
        _controller = null;
        PolicyDraft = null;
        FilesQuery = new();
        await controller.DisposeAsync();
    }

    /// <summary>Retires the active controller and drains its admitted work when the circuit ends.</summary>
    public ValueTask DisposeAsync() => _controller is { } controller ? EndVisitAsync(controller) : ValueTask.CompletedTask;

    private async Task NotifyAsync()
    {
        if (Changed is not { } handlers) return;
        foreach (Func<Task> handler in handlers.GetInvocationList())
            await handler();
    }
}
