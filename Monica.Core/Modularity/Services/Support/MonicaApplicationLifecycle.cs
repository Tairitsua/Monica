using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monica.Core.TypeDiscovery.Abstractions;

namespace Monica.Core.Modularity.Services.Support;

/// <summary>
/// Connects one <see cref="MonicaApplication"/> to Generic Host readiness and shutdown boundaries.
/// </summary>
internal sealed class MonicaApplicationLifecycle(
    MonicaApplication application,
    IHostApplicationLifetime applicationLifetime) : IHostedLifecycleService, IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenRegistration _applicationStartedRegistration;
    private bool _isDisposed;
    private bool _isRegistered;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_isRegistered)
            {
                return Task.CompletedTask;
            }

            _applicationStartedRegistration = applicationLifetime.ApplicationStarted.UnsafeRegister(
                static state =>
                {
                    var app = (MonicaApplication)state!;
                    app.Profiling.MarkApplicationReady();
                    var held = app.ActivationResources.Where(resource => resource.Mode != ActivationMode.Automatic)
                        .Select(resource => $"{resource.Key} [{resource.Kind}, {resource.Mode}]").ToArray();
                    if (held.Length != 0)
                        app.CreateLogger<MonicaApplicationLifecycle>().LogInformation(
                            "Automatic execution is held for {Count} resource(s): {Resources}", held.Length, string.Join("; ", held));
                },
                application);
            _isRegistered = true;
        }

        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        application.Modules.DrainStartupWork();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            if (_isRegistered)
            {
                _applicationStartedRegistration.Dispose();
            }

            _isRegistered = false;
        }
    }
}
