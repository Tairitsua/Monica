using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Locker.Facades;
using Monica.Locker.Leases.Diagnostics;
using Monica.Locker.Services;

namespace Monica.Modules;

/// <summary>Registers the optional, authorized lease-observation boundary independently of critical-section locks.</summary>
public static class ModuleLeaseDiagnosticsBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Adds lease diagnostics; configure explicit keyed store bindings and a scoped access evaluator.</summary>
        public ModuleRegistration<ModuleLeaseDiagnostics, ModuleLeaseDiagnosticsOption> AddLeaseDiagnostics(
            Action<ModuleLeaseDiagnosticsOption>? configure = null) =>
            builder.AddModule<ModuleLeaseDiagnostics, ModuleLeaseDiagnosticsOption>(configure);
    }
}

/// <summary>Composes a shared host-local inventory cache and a caller-scoped authorized facade.</summary>
public sealed class ModuleLeaseDiagnostics : MonicaModule<ModuleLeaseDiagnosticsOption>
{
    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleLeaseDiagnosticsOption> context)
    {
        context.Services.TryAddSingleton(TimeProvider.System);
        context.Services.AddSingleton<LeaseInventoryReader>();
        context.Services.TryAddScoped<ILeaseDiagnosticsAccess, DenyLeaseDiagnosticsAccess>();
        context.Services.AddScoped(provider => new LeaseDiagnosticsFacade(
            provider.GetRequiredService<LeaseInventoryReader>(),
            provider.GetRequiredService<ILeaseDiagnosticsAccess>(),
            provider.GetRequiredService<ILogger<LeaseDiagnosticsFacade>>()));
    }
}

/// <summary>Configures bounded diagnostic presentation and explicit authority access.</summary>
public sealed class ModuleLeaseDiagnosticsOption : ModuleOptions<ModuleLeaseDiagnostics>
{
    /// <summary>Gets explicit store/scope/prefix bindings; empty by default. IDs must be unique.</summary>
    public List<LeaseAuthority> Authorities { get; } = [];
    /// <summary>Gets or sets the minimum interval between inventory attempts per authority; defaults to 15 seconds.</summary>
    /// <remarks>Successes and failures share this cache. Manual UI refresh also respects it. Must be positive.</remarks>
    public TimeSpan InventoryCacheDuration { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Gets or sets the provider cancellation deadline per read; defaults to 10 seconds and must be positive.</summary>
    /// <remarks>A cancellation-ignoring provider retains its single-flight gate until its actual task completes.</remarks>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Gets or sets the retained record limit per authority; defaults to 500, range 1–10000.</summary>
    /// <remarks>Bounds cache and UI size, not the underlying provider's enumeration. Redis still performs SCAN.</remarks>
    public int MaxRecordsPerAuthority { get; set; } = 500;
}
