using Monica.Core.Results;
using Monica.ServiceDiscovery.Facades;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.UIServiceDiscovery.Support;

namespace Monica.ServiceDiscovery.UIServiceDiscovery.State;

/// <summary>
/// Circuit-owned registry evidence and filters. Components own their polling loops and cancellation;
/// this state serializes captures and never retains a component callback or timer.
/// </summary>
public sealed class ServiceDiscoveryPageState(ServiceDiscoveryFacade facade, ServiceInstanceEvictionTracker evictionTracker)
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private bool _allDomainsSelected = true;
    public string UnknownDomainLabel { get; set; } = "Unknown";
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime? CapturedAt { get; private set; }
    public List<RegisteredServiceStatus> Services { get; private set; } = [];
    public List<RegisteredServiceStatus> FilteredServices { get; private set; } = [];
    public bool AutoRefreshEnabled { get; set; } = true;
    public int RefreshInterval { get; set; } = 3;
    public string FilterText { get; set; } = string.Empty;
    public HashSet<ServiceStatus> SelectedStatuses { get; set; } = Enum.GetValues<ServiceStatus>().ToHashSet();
    public HashSet<string> SelectedDomains { get; set; } = new(StringComparer.Ordinal);
    public bool ShowOnlyWithInstances { get; set; }

    /// <summary>Captures one complete registry, retaining prior evidence on failure and rejecting canceled work.</summary>
    public async Task<Res<List<RegisteredServiceStatus>>> RefreshServicesAsync(bool showLoading = true, CancellationToken cancellationToken = default)
    {
        await _captureGate.WaitAsync(cancellationToken);
        try
        {
            IsLoading = showLoading;
            var result = await facade.CaptureServicesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsFailed(out var error, out var services))
            {
                ErrorMessage = error.Message;
                return result;
            }

            Services = evictionTracker.Apply(services ?? []);
            CapturedAt = DateTime.UtcNow;
            ErrorMessage = null;
            if (_allDomainsSelected)
                SelectedDomains = Services.SelectMany(GetServiceDomainNames).ToHashSet(StringComparer.Ordinal);
            ApplyFilters();
            return result;
        }
        finally
        {
            IsLoading = false;
            _captureGate.Release();
        }
    }

    /// <summary>Updates a deliberate domain selection, including an empty selection that means none.</summary>
    public void SetSelectedDomains(IEnumerable<string> domains, bool selectAll = false)
    {
        SelectedDomains = domains.ToHashSet(StringComparer.Ordinal);
        _allDomainsSelected = selectAll;
    }

    /// <summary>Restores all-domain matching, including domains arriving in later successful captures.</summary>
    public void SelectAllDomains()
    {
        _allDomainsSelected = true;
        SelectedDomains = Services.SelectMany(GetServiceDomainNames).ToHashSet(StringComparer.Ordinal);
    }

    public void ApplyFilters()
    {
        var query = FilterText.Trim();
        FilteredServices = Services.Where(service =>
            SelectedStatuses.Contains(service.OverallStatus) &&
            GetServiceDomainNames(service).Any(SelectedDomains.Contains) &&
            (!ShowOnlyWithInstances || service.TotalInstanceCount > 0) &&
            (query.Length == 0 || service.AppName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             service.AppId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             service.ProjectName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
             GetServiceDomainNames(service).Any(domain => domain.Contains(query, StringComparison.OrdinalIgnoreCase)))).ToList();
    }

    private IEnumerable<string> GetServiceDomainNames(RegisteredServiceStatus service) => service.GetDomainNames().DefaultIfEmpty(UnknownDomainLabel);
}
