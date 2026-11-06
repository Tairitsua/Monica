using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.ServiceDiscovery.Localization;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Services;
using Monica.Core.Results;

namespace Monica.ServiceDiscovery.Facades;

public sealed class ServiceDiscoveryFacade(
    ServiceDiscoveryQueryService queryService,
    IOptions<ModuleServiceDiscoveryOption> options,
    IStringLocalizer<ServiceDiscoveryResource> localizer,
    ILogger<ServiceDiscoveryFacade> logger)
{
    private readonly ModuleServiceDiscoveryOption _option = options.Value;

    /// <summary>
    /// Captures detached current registrations and predefined services. Failed reads return an error,
    /// rather than a successful empty registry. Cancellation prevents the UI from adopting retired work.
    /// </summary>
    public Task<Result<List<RegisteredServiceStatus>>> CaptureServicesAsync(CancellationToken cancellationToken = default)
        => CaptureAsync(async () => (await queryService.GetMergedServicesStatusAsync(cancellationToken))
            .Select(ServiceDiscoverySnapshot.Copy).ToList(), cancellationToken);

    /// <summary>Captures detached catalog entries. A successful empty catalog remains distinct from a failed read.</summary>
    public Task<Result<List<DomainInfo>>> CaptureDomainsAsync(CancellationToken cancellationToken = default)
        => CaptureAsync(async () => (await queryService.GetDomainsAsync(cancellationToken)).Select(domain => new DomainInfo
        {
            Id = domain.Id, DisplayName = domain.DisplayName, Description = domain.Description
        }).ToList(), cancellationToken);

    /// <summary>Captures querying-host identity and local election evidence alongside the independently read application lease.</summary>
    public Task<Result<CurrentInstanceSnapshot>> CaptureCurrentInstanceAsync(CancellationToken cancellationToken = default)
        => CaptureAsync(() => queryService.GetCurrentInstanceSnapshotAsync(cancellationToken), cancellationToken);

    /// <summary>Captures compiled API usage separately from live registration and transport evidence.</summary>
    public Task<Result<ApplicationTopologySnapshot>> CaptureTopologyAsync(CancellationToken cancellationToken = default)
        => CaptureAsync(() => Task.FromResult(queryService.GetApplicationTopology()), cancellationToken);

    private async Task<Result<T>> CaptureAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            var captured = await read();
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Success(captured);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogError(exception, "Failed to capture discovery evidence.");
            return Result.Failure<T>(localizer["Workbench:ReadFailed"].Value);
        }
    }

    public async Task<Result<List<RegisteredServiceStatus>>> GetServicesStatusAsync()
    {
        try
        {
            return Result.Success(await queryService.GetServicesStatusAsync());
        }
        catch (InvalidOperationException)
        {
            return Result.Failure<List<RegisteredServiceStatus>>(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get service status.");
            return Result.Failure<List<RegisteredServiceStatus>>(localizer["Service:Errors:GetServicesStatusFailed", ex.Message].Value);
        }
    }

    public async Task<Result<LeaderStatusResponse>> GetRegistryLeaderStatusAsync()
    {
        try
        {
            return Result.Success(await queryService.GetRegistryLeaderStatusAsync());
        }
        catch (InvalidOperationException)
        {
            return Result.Failure<LeaderStatusResponse>(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get registry leader status.");
            return Result.Failure<LeaderStatusResponse>($"获取 Leader 状态失败: {ex.Message}");
        }
    }

    public async Task<Result<RegistryServiceStatusResponse>> GetRegistryServiceStatusAsync()
    {
        try
        {
            return Result.Success(await queryService.GetRegistryServiceStatusAsync());
        }
        catch (InvalidOperationException)
        {
            return Result.Failure<RegistryServiceStatusResponse>(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get registry service status.");
            return Result.Failure<RegistryServiceStatusResponse>($"获取服务状态失败: {ex.Message}");
        }
    }

    public async Task<Result> ReleaseLeaderAsync()
    {
        try
        {
            if (!await queryService.ReleaseLeaderAsync())
            {
                return Result.Failure("当前实例不是 Leader");
            }

            return Result.Success().WithMessage("已释放 Leader 状态");
        }
        catch (InvalidOperationException)
        {
            return Result.Failure(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to release leader status.");
            return Result.Failure($"释放 Leader 状态失败: {ex.Message}");
        }
    }

    public Result<ElectionConfig> GetElectionConfig()
    {
        return Result.Success(_option.Election);
    }

    public async Task<Result<List<RegisteredServiceStatus>>> GetMergedServicesStatusAsync()
    {
        try
        {
            return Result.Success(await queryService.GetMergedServicesStatusAsync());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get merged service status.");
            return Result.Failure<List<RegisteredServiceStatus>>(localizer["Service:Errors:GetMergedServicesStatusFailed", ex.Message].Value);
        }
    }

    public async Task<Result<List<DomainInfo>>> GetDomainsAsync()
    {
        try
        {
            return Result.Success(await queryService.GetDomainsAsync());
        }
        catch (InvalidOperationException)
        {
            return Result.Failure<List<DomainInfo>>(localizer["Service:Errors:InfoProviderNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get service discovery domains.");
            return Result.Failure<List<DomainInfo>>(localizer["Service:Errors:GetDomainsFailed", ex.Message].Value);
        }
    }

    public async Task<Result<DomainDetailInfo>> GetDomainDetailAsync(string domainName)
    {
        if (string.IsNullOrWhiteSpace(domainName))
        {
            return Result.Failure<DomainDetailInfo>(localizer["Service:Errors:DomainNameRequired"].Value);
        }

        try
        {
            return Result.Success(await queryService.GetDomainDetailAsync(domainName));
        }
        catch (KeyNotFoundException)
        {
            return Result.Failure<DomainDetailInfo>(localizer["Service:Errors:DomainNotFound", domainName].Value);
        }
        catch (InvalidOperationException)
        {
            return Result.Failure<DomainDetailInfo>(localizer["Service:Errors:InfoProviderNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get domain detail for {DomainName}.", domainName);
            return Result.Failure<DomainDetailInfo>(localizer["Service:Errors:GetDomainDetailFailed", ex.Message].Value);
        }
    }

    public async Task<Result<LeaderState?>> GetLeaderStateAsync()
    {
        try
        {
            return Result.Success(await queryService.GetLeaderStateAsync());
        }
        catch (InvalidOperationException)
        {
            return Result.Failure<LeaderState?>(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get leader state.");
            return Result.Failure<LeaderState?>(localizer["Service:Errors:GetLeaderStateFailed", ex.Message].Value);
        }
    }

    public async Task<Result<CurrentInstanceSnapshot>> GetCurrentInstanceSnapshotAsync()
    {
        try
        {
            return Result.Success(await queryService.GetCurrentInstanceSnapshotAsync());
        }
        catch (InvalidOperationException)
        {
            return Result.Failure<CurrentInstanceSnapshot>(localizer["CurrentInstance:Errors:ClientInfoNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get current instance snapshot.");
            return Result.Failure<CurrentInstanceSnapshot>(localizer["CurrentInstance:Errors:LoadFailed", ex.Message].Value);
        }
    }

    public async Task<Result> ForceDeleteLeaderAsync(string appId)
    {
        try
        {
            await queryService.ForceDeleteLeaderAsync(appId);
            return Result.Success();
        }
        catch (InvalidOperationException)
        {
            return Result.Failure(localizer["Service:Errors:StateManagerNotConfigured"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to force delete leader for application {AppId}.", appId);
            return Result.Failure(localizer["Service:Errors:ForceDeleteLeaderFailed", ex.Message].Value);
        }
    }
}
