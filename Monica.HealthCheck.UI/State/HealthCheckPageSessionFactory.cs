using Microsoft.Extensions.Hosting;
using Monica.Core.Results;
using Monica.HealthCheck.Facades;
using Monica.HealthCheck.Models;

namespace Monica.HealthCheck.UI.State;

/// <summary>
/// Creates explicitly page-owned Health Check sessions without extending their disposable lifetime through dependency injection.
/// </summary>
public sealed class HealthCheckPageSessionFactory
{
    private readonly Func<HealthCheckScope, CancellationToken, Task<Result<HealthCheckSnapshot>>> _getSnapshotAsync;
    private readonly string _hostName;

    /// <summary>
    /// Creates a factory backed by the host-local Health Check facade.
    /// </summary>
    /// <param name="facade">The sanitized current-host Health Check facade.</param>
    /// <param name="environment">The current Monica host environment.</param>
    public HealthCheckPageSessionFactory(HealthCheckFacade facade, IHostEnvironment environment)
        : this(
            facade.GetSnapshotAsync,
            $"{environment.ApplicationName} · {Environment.MachineName}")
    {
    }

    internal HealthCheckPageSessionFactory(
        Func<HealthCheckScope, CancellationToken, Task<Result<HealthCheckSnapshot>>> getSnapshotAsync,
        string hostName)
    {
        _getSnapshotAsync = getSnapshotAsync ?? throw new ArgumentNullException(nameof(getSnapshotAsync));
        _hostName = hostName;
    }

    /// <summary>
    /// Creates a fresh session owned by one rendered page instance.
    /// </summary>
    /// <param name="authorize">
    /// Re-evaluates current-circuit access before every facade boundary so an authentication change takes effect without
    /// allowing a protected health snapshot to be evaluated first.
    /// </param>
    public HealthCheckPageSession Create(Func<CancellationToken, Task<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        return new HealthCheckPageSession(_getSnapshotAsync, authorize, _hostName);
    }
}
