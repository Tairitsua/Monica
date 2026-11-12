using Monica.Core.Execution.Facades;
using Monica.Core.Execution.Models;
using Monica.Core.Results;
using Monica.Framework.UI.UIExecutionPipeline.Models;

namespace Monica.Framework.UI.UIExecutionPipeline.State;

/// <summary>
/// Owns loading, filtering, and selection state for the execution-pipeline catalog page.
/// </summary>
public sealed class ExecutionPipelinePageState(ExecutionPipelineCatalogFacade catalogFacade)
{
    /// <summary>
    /// Gets the latest host-local execution-pipeline catalog snapshot.
    /// </summary>
    public ExecutionPipelineCatalogSnapshot? Snapshot { get; private set; }

    /// <summary>
    /// Gets whether the page is currently loading a snapshot.
    /// </summary>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// Gets the last refresh error. The previous successful snapshot remains available when a refresh fails.
    /// </summary>
    public string? LoadError { get; private set; }

    /// <summary>
    /// Gets the selected observed execution plan.
    /// </summary>
    public ExecutionPipelinePlanSnapshot? SelectedPlan { get; private set; }

    /// <summary>
    /// Gets the current fuzzy-search value.
    /// </summary>
    public string SearchText { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the selected execution-point value. An empty value includes all points.
    /// </summary>
    public string ExecutionPointFilter { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the selected business-operation filter.
    /// </summary>
    public ExecutionPipelineBusinessFilter BusinessFilter { get; private set; }

    /// <summary>
    /// Gets the selected transaction-mode name. An empty value includes all modes.
    /// </summary>
    public string TransactionModeFilter { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the selected plan-status name. An empty value includes all statuses.
    /// </summary>
    public string StatusFilter { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the distinct execution points represented by the current snapshot.
    /// </summary>
    public IReadOnlyList<string> AvailableExecutionPoints => Snapshot?.Plans
        .Select(static plan => plan.Descriptor.Point.Value)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray() ?? [];

    /// <summary>
    /// Gets the distinct transaction modes represented by the current snapshot.
    /// </summary>
    public IReadOnlyList<string> AvailableTransactionModes => Snapshot?.Plans
        .Select(static plan => plan.Descriptor.TransactionMode.ToString())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray() ?? [];

    /// <summary>
    /// Gets the distinct cache statuses represented by the current snapshot.
    /// </summary>
    public IReadOnlyList<string> AvailableStatuses => Snapshot?.Plans
        .Select(static plan => plan.Status.ToString())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray() ?? [];

    /// <summary>
    /// Gets the observed plans matching all current filters.
    /// </summary>
    public IReadOnlyList<ExecutionPipelinePlanSnapshot> FilteredPlans => Snapshot?.Plans
        .Where(MatchesFilters)
        .ToArray() ?? [];

    /// <summary>
    /// Raised after a state transition that requires the page to render again.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Loads the initial catalog snapshot.
    /// </summary>
    /// <returns><see langword="true"/> when the snapshot was loaded successfully.</returns>
    public Task<bool> InitializeAsync() => RefreshAsync();

    /// <summary>
    /// Refreshes the catalog snapshot while preserving the previous snapshot if loading fails.
    /// </summary>
    /// <returns><see langword="true"/> when the snapshot was refreshed successfully.</returns>
    public async Task<bool> RefreshAsync()
    {
        IsLoading = true;
        LoadError = null;
        NotifyStateChanged();

        var result = await catalogFacade.GetSnapshotAsync();
        var error = result.WithoutData();
        var snapshot = result.Data;
        if (result.IsFailure() || snapshot is null)
        {
            IsLoading = false;
            LoadError = error.Message ?? "The execution pipeline snapshot was unavailable.";
            NotifyStateChanged();
            return false;
        }

        Snapshot = snapshot;
        IsLoading = false;
        LoadError = null;
        NormalizeSelection();
        NotifyStateChanged();
        return true;
    }

    /// <summary>
    /// Updates the fuzzy-search text.
    /// </summary>
    public void SetSearchText(string? value)
    {
        SearchText = value?.Trim() ?? string.Empty;
        FiltersChanged();
    }

    /// <summary>
    /// Updates the execution-point filter.
    /// </summary>
    public void SetExecutionPointFilter(string? value)
    {
        ExecutionPointFilter = value ?? string.Empty;
        FiltersChanged();
    }

    /// <summary>
    /// Updates the business-operation filter.
    /// </summary>
    public void SetBusinessFilter(ExecutionPipelineBusinessFilter value)
    {
        BusinessFilter = value;
        FiltersChanged();
    }

    /// <summary>
    /// Updates the transaction-mode filter.
    /// </summary>
    public void SetTransactionModeFilter(string? value)
    {
        TransactionModeFilter = value ?? string.Empty;
        FiltersChanged();
    }

    /// <summary>
    /// Updates the plan-status filter.
    /// </summary>
    public void SetStatusFilter(string? value)
    {
        StatusFilter = value ?? string.Empty;
        FiltersChanged();
    }

    /// <summary>
    /// Selects an observed execution plan by its compact stable identifier.
    /// </summary>
    public void SelectPlan(ExecutionPipelinePlanSnapshot? plan)
    {
        SelectedPlan = plan;
        NotifyStateChanged();
    }

    private void FiltersChanged()
    {
        NormalizeSelection();
        NotifyStateChanged();
    }

    private void NormalizeSelection()
    {
        var filteredPlans = FilteredPlans;
        SelectedPlan = SelectedPlan is null
            ? filteredPlans.FirstOrDefault()
            : filteredPlans.FirstOrDefault(plan => plan.Id == SelectedPlan.Id)
              ?? filteredPlans.FirstOrDefault();
    }

    private bool MatchesFilters(ExecutionPipelinePlanSnapshot plan)
    {
        if (!string.IsNullOrEmpty(ExecutionPointFilter) &&
            !string.Equals(plan.Descriptor.Point.Value, ExecutionPointFilter, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(TransactionModeFilter) &&
            !string.Equals(plan.Descriptor.TransactionMode.ToString(), TransactionModeFilter, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(StatusFilter) &&
            !string.Equals(plan.Status.ToString(), StatusFilter, StringComparison.Ordinal))
        {
            return false;
        }

        if (BusinessFilter == ExecutionPipelineBusinessFilter.BusinessOnly &&
            !plan.Descriptor.IsBusinessOperation)
        {
            return false;
        }

        if (BusinessFilter == ExecutionPipelineBusinessFilter.NonBusinessOnly &&
            plan.Descriptor.IsBusinessOperation)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return Contains(plan.Id.Value) ||
               Contains(plan.Diagnostics.CanonicalKey) ||
               Contains(plan.Descriptor.Id.Value) ||
               Contains(plan.Descriptor.Diagnostics.CanonicalKey) ||
               Contains(plan.Descriptor.Name) ||
               Contains(plan.Descriptor.FullName) ||
               Contains(plan.Descriptor.Point.Value) ||
               Contains(plan.Descriptor.ComponentType.Name) ||
               Contains(plan.Descriptor.ComponentType.FullName) ||
               Contains(plan.Descriptor.ComponentType.AssemblyName) ||
               Contains(plan.Descriptor.ComponentType.Diagnostics.AssemblyQualifiedName) ||
               Contains(plan.Descriptor.ContractType?.Name) ||
               Contains(plan.Descriptor.ContractType?.FullName) ||
               Contains(plan.Descriptor.ContractType?.AssemblyName) ||
               Contains(plan.Descriptor.ContractType?.Diagnostics.AssemblyQualifiedName) ||
               Contains(plan.Descriptor.EntryMethod?.Name) ||
               Contains(plan.Descriptor.EntryMethod?.DisplaySignature) ||
               plan.Behaviors.Any(behavior =>
                   Contains(behavior.RegisteredType.Name) ||
                   Contains(behavior.RegisteredType.FullName) ||
                   Contains(behavior.RegisteredType.AssemblyName) ||
                   Contains(behavior.ResolvedType.Name) ||
                   Contains(behavior.ResolvedType.FullName) ||
                   Contains(behavior.ResolvedType.AssemblyName) ||
                   Contains(behavior.SourceModuleKey?.Value));
    }

    private bool Contains(string? value) =>
        value?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) == true;

    private void NotifyStateChanged() => StateChanged?.Invoke();
}
