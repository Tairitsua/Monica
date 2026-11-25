using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monica.Core.ApplicationRoles;
using Monica.Core.HostedService.Abstractions;

namespace Monica.Core.HostedService.Services.Support;

internal sealed class HostedServiceRoleParticipant(IRoleHealthStateProvider health, IRoleFaultCoordinator coordinator,
    IRoleHostedServiceFaultObserver observer, HostedServiceDescriptorCatalog catalog) : IRoleLifecycleParticipant
{
    private volatile HostedServiceRoleBinding[] _bindings = [];

    internal void Bind(IHostedService[] services)
    {
        var descriptors = catalog.GetDescriptors().Where(descriptor => !descriptor.IsKeyedService).ToArray();
        var bindings = new List<HostedServiceRoleBinding>();
        for (var index = 0; index < services.Length; index++)
        {
            var service = services[index];
            if (service is not (MoHostedService or MoBackgroundService)) continue;
            var role = catalog.GetRoleId(descriptors[index], service.GetType(), health);
            if (role is null || !health.IsRoleActive(role)) continue;
            var binding = new HostedServiceRoleBinding(role, health, coordinator, observer, service);
            ((IMoHostedService)service).RuntimeInfo.Role = binding;
            bindings.Add(binding);
        }
        _bindings = bindings.ToArray();
    }

    public Task OnRoleQuarantinedAsync(string roleId, CancellationToken cancellationToken) =>
        Task.WhenAll(_bindings.Where(binding => binding.RoleId == roleId)
            .Select(binding => binding.DrainAsync(cancellationToken)));
}

internal sealed class HostedServiceRoleBinding(string roleId, IRoleHealthStateProvider health,
    IRoleFaultCoordinator coordinator, IRoleHostedServiceFaultObserver observer, IHostedService service)
{
    private readonly Lock _gate = new();
    private readonly RoleWorkAdmission _work = new();
    private TaskCompletionSource? _startup;
    private TaskCompletionSource? _stopped;

    internal string RoleId => roleId;
    internal bool Quarantined => health.IsRoleFaulted(roleId);
    internal bool Skipped { get; private set; }

    internal bool TryBeginStart()
    {
        lock (_gate)
        {
            if (Quarantined)
            {
                Skipped = true;
                return false;
            }
            _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    internal void CompleteStart() => _startup?.TrySetResult();

    internal bool TryIsolateStartupFault() =>
        coordinator.TryIsolateStartupFault(roleId, RoleFaultRecord.ForResource(RoleFaultStage.HostedServiceStartup, service.GetType()));

    internal async Task ObserveBackgroundFaultAsync(Exception exception, ILogger logger)
    {
        try
        {
            await observer.OnBackgroundExecutionFaultedAsync(roleId, service.GetType(), exception,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception observerFailure)
        {
            logger.LogError(observerFailure, "Failed to observe a background service failure for role {RoleId}", roleId);
        }
    }

    internal IDisposable AdmitWork()
    {
        var admission = _work.TryEnter();
        if (admission is not null && !Quarantined) return admission;
        admission?.Dispose();
        throw new RoleUnavailableException(roleId);
    }

    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        await _work.CloseAndDrainAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        Task? startup;
        lock (_gate) startup = _startup?.Task;
        if (startup is null) return;
        // A concurrent or partially failed start still owns its cleanup. The startup path never waits for this drain.
        await startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        await service.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task StopOnceAsync(Func<Task> stop)
    {
        TaskCompletionSource completion;
        bool first;
        lock (_gate)
        {
            first = _stopped is null;
            completion = _stopped ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (first)
        {
            try { await stop().ConfigureAwait(false); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }
        await completion.Task.ConfigureAwait(false);
    }
}
