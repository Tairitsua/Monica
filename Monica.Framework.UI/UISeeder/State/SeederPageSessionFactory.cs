using Microsoft.Extensions.Hosting;
using Monica.Core.Results;
using Monica.Framework.Seeder.Facades;
using Monica.Framework.Seeder.Models;

namespace Monica.Framework.UI.UISeeder.State;

/// <summary>
/// Creates explicitly page-owned Seeder sessions without extending their disposable lifetime through dependency injection.
/// </summary>
public sealed class SeederPageSessionFactory
{
    private readonly Func<CancellationToken, Task<Res<SeederDiagnosticsSnapshot>>> _getSnapshotAsync;
    private readonly Func<string, CancellationToken, Task<Res<SeederStateSnapshot>>>? _runAsync;
    private readonly string _hostName;

    /// <summary>Creates a factory backed by the host-local Seeder diagnostics facade.</summary>
    /// <param name="facade">The sanitized, current-host Seeder facade.</param>
    /// <param name="environment">The current Monica host environment.</param>
    public SeederPageSessionFactory(SeederFacade facade, IHostEnvironment environment)
        : this(
            facade.GetSnapshotAsync,
            $"{environment.ApplicationName} · {Environment.MachineName}", facade.RunAsync)
    {
    }

    internal SeederPageSessionFactory(
        Func<CancellationToken, Task<Res<SeederDiagnosticsSnapshot>>> getSnapshotAsync,
        string hostName,
        Func<string, CancellationToken, Task<Res<SeederStateSnapshot>>>? runAsync = null)
    {
        _getSnapshotAsync = getSnapshotAsync ?? throw new ArgumentNullException(nameof(getSnapshotAsync));
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        _hostName = hostName;
        _runAsync = runAsync;
    }

    /// <summary>Creates a fresh session owned by one rendered page instance.</summary>
    /// <param name="authorize">
    /// Re-evaluates current-circuit access before every facade boundary so authentication changes take effect before
    /// protected Seeder diagnostics are requested.
    /// </param>
    public SeederPageSession Create(Func<CancellationToken, Task<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        return new SeederPageSession(_getSnapshotAsync, authorize, _hostName, runAsync: _runAsync);
    }
}
