using Microsoft.Extensions.Logging;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Diagnostics.Services;
using Monica.Core.Modularity.Models;
using Monica.Core.Modularity.Metrics;
using Monica.Core.Results;

namespace Monica.Core.Modularity.Diagnostics.Facades;

/// <summary>
/// Provides synchronous, read-only module diagnostics for local UI and host integration boundaries.
/// </summary>
public sealed class ModuleDiagnosticsFacade
{
    private readonly ModuleDiagnosticsService _diagnostics;
    private readonly ModuleInitMetrics _metrics;
    private readonly ILogger<ModuleDiagnosticsFacade> _logger;

    internal ModuleDiagnosticsFacade(
        ModuleDiagnosticsService diagnostics,
        ModuleInitMetrics metrics,
        ILogger<ModuleDiagnosticsFacade> logger)
    {
        _diagnostics = diagnostics;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Gets one immutable, revisioned diagnostics snapshot.</summary>
    public Res<ModuleDiagnosticsSnapshot> GetSnapshot()
    {
        return Execute(
            () =>
            {
                var snapshot = _diagnostics.GetSnapshot();
                _metrics.Observe(snapshot);
                return snapshot;
            },
            "Failed to build the module diagnostics snapshot.");
    }

    /// <summary>Gets the lazily cached assembly-resolution and type-scan inventory.</summary>
    public Res<TypeDiscoveryAssemblyInventory> GetAssemblyInventory()
    {
        return Execute(
            _diagnostics.GetAssemblyInventory,
            "Failed to load the type-discovery assembly inventory.");
    }

    /// <summary>Gets current per-resource activation observations independently of the immutable startup snapshot.</summary>
    /// <remarks>Held factories remain opaque. Native prerequisite updates can change later observations; reading never starts work.</remarks>
    public Res<IReadOnlyList<ResourceActivationObservation>> GetResourceActivations() => Execute(
        _diagnostics.GetResourceActivations, "Failed to load resource activation observations.");

    /// <summary>
    /// Gets a complete public-property catalog with bounded values for one module's finalized default options.
    /// </summary>
    /// <param name="moduleKey">The host-local module diagnostic key.</param>
    public Res<ModuleOptionDiagnostics> GetModuleOptions(ModuleKey moduleKey)
    {
        return Execute(
            () => _diagnostics.GetModuleOptions(moduleKey),
            "Failed to load the module option diagnostics.");
    }

    /// <summary>
    /// Gets a complete public-property catalog with bounded values for a named option profile or an explicitly
    /// permitted default fallback.
    /// </summary>
    /// <param name="moduleKey">The host-local module diagnostic key.</param>
    /// <param name="selector">The exact profile-selection and fallback behavior.</param>
    public Res<ModuleOptionDiagnostics> GetModuleOptions(
        ModuleKey moduleKey,
        ModuleOptionProfileSelector selector)
    {
        return Execute(
            () =>
            {
                ArgumentNullException.ThrowIfNull(selector);
                return _diagnostics.GetModuleOptions(moduleKey, selector);
            },
            "Failed to load the module option diagnostics.");
    }

    /// <summary>
    /// Creates a portable baseline without option values, assembly paths, stack traces, or raw exception details.
    /// </summary>
    public Res<ModuleDiagnosticsExport> CreateExport()
    {
        return Execute(
            _diagnostics.CreateExport,
            "Failed to create the module diagnostics export.");
    }

    private Res<T> Execute<T>(Func<T> action, string publicFailureMessage)
    {
        try
        {
            return Res.Ok(action());
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "{FailureMessage}", publicFailureMessage);
            return Res.Fail(publicFailureMessage);
        }
    }
}
