using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Modules;

namespace Monica.DataChannel.Services;

/// <summary>Coordinates startup and shutdown through the same owner used by explicit setup commands.</summary>
public sealed class DataChannelInitializerService : MoHostedService
{
    private readonly DataChannelRuntime _runtime;

    internal DataChannelInitializerService(DataChannelRuntime runtime, IObservableInstanceRegistry observableManager,
        IOptions<ModuleHostedServiceOption> hostedServiceOptions, IServiceScopeFactory serviceScopeFactory,
        ILogger<DataChannelInitializerService> logger)
        : base(observableManager, hostedServiceOptions, serviceScopeFactory, logger) => _runtime = runtime;

    /// <inheritdoc />
    public override string ServiceName => nameof(DataChannelInitializerService);
    /// <inheritdoc />
    public override string? ServiceGroupId => nameof(ModuleDataChannel);

    /// <inheritdoc />
    protected override async Task OnStartingAsync(CancellationToken cancellationToken)
    {
        RecordState("Initializing automatic data-channel setups.", HostedServiceState.Executing);
        var failures = await _runtime.InitializeAutomaticAsync(cancellationToken);
        RecordState(failures == 0 ? "Data-channel initialization completed."
            : $"Data-channel initialization completed with {failures} failed setup(s).",
            failures == 0 ? HostedServiceState.Running : HostedServiceState.Degraded);
    }

    /// <inheritdoc />
    protected override Task OnStoppingAsync(CancellationToken cancellationToken) => _runtime.ShutdownAsync();
}
