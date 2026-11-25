using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monica.Core.ApplicationRoles;

namespace Monica.ApplicationModel.Services;

internal sealed class RoleFaultCoordinator(
    CompiledApplicationModel model,
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<RoleFaultCoordinator> logger) : IRoleFaultCoordinator, IRoleHostedServiceFaultObserver, IAsyncDisposable, IDisposable
{
    private readonly Lock _pendingGate = new();
    private readonly Dictionary<string, Task> _drains = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _teardown = new();
    private Task? _disposal;

    public bool TryIsolateStartupFault(string roleId, RoleFaultRecord fault)
    {
        lock (_pendingGate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            var (isolated, transitioned) = model.RecordStartupResourceFault(roleId, fault);
            if (transitioned) QueueDrain(roleId, stopHost: false);
            return isolated;
        }
    }

    public void ReportRuntimeResourceFault(string roleId, bool isRoleCriticalResource, RoleFaultRecord fault)
        => _ = PublishRuntimeFault(roleId, isRoleCriticalResource, fault);

    public Task ReportRuntimeResourceFaultAsync(string roleId, bool isRoleCriticalResource,
        RoleFaultRecord fault, CancellationToken cancellationToken = default) =>
        PublishRuntimeFault(roleId, isRoleCriticalResource, fault).WaitAsync(cancellationToken);

    private Task PublishRuntimeFault(string roleId, bool critical, RoleFaultRecord fault)
    {
        lock (_pendingGate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (model.RecordRuntimeFault(roleId, fault, critical))
                return QueueDrain(roleId, model.StopAfterRuntimeQuarantine);
            return _drains.GetValueOrDefault(roleId, Task.CompletedTask);
        }
    }

    // The reporting work can itself own a delivery lease or a background loop. Its thread must stay free to exit.
    private Task QueueDrain(string roleId, bool stopHost)
    {
        var drain = Task.Run(() => DrainRoleAsync(roleId, stopHost));
        _drains.Add(roleId, drain);
        return drain;
    }

    public Task OnBackgroundExecutionFaultedAsync(string roleId, Type serviceType, Exception exception,
        CancellationToken cancellationToken)
    {
        ReportRuntimeResourceFault(roleId, serviceType.IsDefined(typeof(RoleCriticalResourceAttribute), inherit: true),
            RoleFaultRecord.ForResource(RoleFaultStage.RuntimeResource, serviceType));
        return Task.CompletedTask;
    }

    private async Task DrainRoleAsync(string roleId, bool stopHost)
    {
        try
        {
            _teardown.Token.ThrowIfCancellationRequested();
            var participants = services.GetServices<IRoleLifecycleParticipant>();
            await Task.WhenAll(participants.Select(async participant =>
                await participant.OnRoleQuarantinedAsync(roleId, _teardown.Token).ConfigureAwait(false))).ConfigureAwait(false);
            if (stopHost) lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (_teardown.IsCancellationRequested)
        {
            // A failed host startup can dispose the container before signaling ApplicationStarted.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to drain quarantined role {RoleId}. Admission remains closed.", roleId);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_pendingGate) return new ValueTask(_disposal ??= DisposeDrainsAsync([.. _drains.Values]));
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task DisposeDrainsAsync(Task[] drains)
    {
        try
        {
            await _teardown.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(drains).ConfigureAwait(false); }
            catch (Exception) { /* Each drain logs its own failure. Continue disposing other host resources. */ }
        }
        finally { _teardown.Dispose(); }
    }
}
