using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Results;
using Monica.DependencyInjection.Facades;
using Monica.Framework.UI.UIDependencyInjection.Models;

namespace Monica.Framework.UI.UIDependencyInjection.State;

internal sealed class DependencyInjectionPageSession(IServiceProvider services)
{
    private bool _initialized;

    public DependencyInjectionWorkbenchSnapshot? Snapshot { get; private set; }
    public bool Busy { get; private set; }
    public bool Failed { get; private set; }
    public bool Unavailable { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public Task InitializeAsync() => _initialized ? Task.CompletedTask : RefreshAsync();

    public async Task RefreshAsync()
    {
        if (Busy)
        {
            return;
        }

        var facade = services.GetService<DependencyInjectionDiagnosticsFacade>();
        Unavailable = facade is null;
        _initialized = true;
        if (facade is null)
        {
            return;
        }

        Busy = true;
        try
        {
            var result = await facade.GetSnapshotAsync();
            Failed = result.IsFailure() || result.Data is null;
            if (!Failed && result.Data is { } snapshot)
            {
                if (!ReferenceEquals(Snapshot?.Raw, snapshot))
                {
                    Snapshot = new(snapshot);
                }
                ReadAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            Busy = false;
        }
    }
}
