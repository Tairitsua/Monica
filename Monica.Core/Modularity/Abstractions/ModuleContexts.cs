using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Models.Internal;
using Monica.Core.Modularity.Services.Support;

namespace Monica.Core.Modularity.Abstractions;

/// <summary>
/// Reads finalized options through relationships declared by the current module.
/// </summary>
public interface IModuleOptionReader
{
    /// <summary>
    /// Gets the options of a directly required module.
    /// </summary>
    TOptions Get<TModule, TOptions>()
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new();

    /// <summary>
    /// Attempts to get the options of a directly ordered module when it is active.
    /// </summary>
    bool TryGet<TModule, TOptions>(out TOptions? options)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new();
}

/// <summary>
/// Exposes the finalized option object and host-bound module graph to a composition callback.
/// </summary>
/// <typeparam name="TOptions">The current module's option type.</typeparam>
public abstract class ModuleCompositionContext<TOptions> : IMonicaCompositionContext
    where TOptions : class, IModuleOptions, new()
{
    internal ModuleCompositionContext(ModuleConfigurationContext context)
    {
        Composition = context.Registration.Application.Composition;
        RegistrationOwner = context.RegistrationOwner;
        Options = (TOptions)context.Registration.ModuleOption;
        Modules = new ModuleOptionReader(
            context.Registration.Application,
            context.Registration.ModuleType);
    }

    /// <summary>
    /// Gets the current module's finalized option object.
    /// </summary>
    public TOptions Options { get; }

    /// <inheritdoc />
    public MonicaCompositionState Composition { get; }

    /// <summary>Gets the declaring application type captured by this contribution, or null for module-wide lifecycle callbacks.</summary>
    /// <remarks>Resource registrations retain their own owner; a framework module may contain contributions from several owners.</remarks>
    public Type? RegistrationOwner { get; }

    /// <summary>
    /// Gets the relationship-checked option reader for the current module.
    /// </summary>
    public IModuleOptionReader Modules { get; }
}

/// <summary>
/// Context for deterministic service registration callbacks.
/// </summary>
public sealed class ModuleContext<TOptions> : ModuleCompositionContext<TOptions>
    where TOptions : class, IModuleOptions, new()
{
    internal ModuleContext(ModuleConfigurationContext context) : base(context)
    {
        Services = context.Services
            ?? throw new InvalidOperationException("Service registration is unavailable in this module phase.");
    }

    /// <summary>
    /// Gets the owning host's service collection.
    /// </summary>
    public IServiceCollection Services { get; }
}

/// <summary>
/// Context for a deterministic business-type discovery commit.
/// </summary>
/// <typeparam name="TOptions">The current module's option type.</typeparam>
/// <remarks>
/// Discovery commits expose only the indexed writer. Keeping the raw service collection outside this boundary
/// guarantees that the writer's service-identity index remains authoritative throughout the high-volume commit path.
/// </remarks>
public sealed class TypeDiscoveryContext<TOptions> : ModuleCompositionContext<TOptions>
    where TOptions : class, IModuleOptions, new()
{
    internal TypeDiscoveryContext(ModuleConfigurationContext context) : base(context)
    {
        var services = context.Services
            ?? throw new InvalidOperationException("Service registration is unavailable during type discovery.");
        Registrations = context.Registration.Application.Modules.GetRegistrationWriter(services);
    }

    /// <summary>
    /// Gets the indexed writer used by high-volume discovery commits.
    /// </summary>
    public ModuleServiceRegistrationWriter Registrations { get; }
}

/// <summary>
/// Context for host-builder configuration callbacks.
/// </summary>
public sealed class ModuleBuilderContext<TOptions> : ModuleCompositionContext<TOptions>
    where TOptions : class, IModuleOptions, new()
{
    internal ModuleBuilderContext(ModuleConfigurationContext context) : base(context)
    {
        HostApplicationBuilder = context.HostApplicationBuilder
            ?? throw new InvalidOperationException("The host builder is unavailable in this module phase.");
    }

    /// <summary>
    /// Gets the host application builder being composed.
    /// </summary>
    public IHostApplicationBuilder HostApplicationBuilder { get; }
}

/// <summary>
/// Context for ASP.NET Core middleware and endpoint contributions.
/// </summary>
public sealed class WebModuleContext<TOptions> : ModuleCompositionContext<TOptions>
    where TOptions : class, IModuleOptions, new()
{
    internal WebModuleContext(ModuleConfigurationContext context) : base(context)
    {
        ApplicationBuilder = context.ApplicationBuilder
            ?? throw new InvalidOperationException("The ASP.NET Core application is unavailable in this module phase.");
    }

    /// <summary>
    /// Gets the ASP.NET Core application builder.
    /// </summary>
    public IApplicationBuilder ApplicationBuilder { get; }

    /// <summary>
    /// Gets the concrete web application or throws when the adapter is not a <see cref="WebApplication"/>.
    /// </summary>
    public WebApplication RequireWebApplication()
    {
        return ApplicationBuilder as WebApplication
            ?? throw new InvalidOperationException("This contribution requires a WebApplication host adapter.");
    }
}

internal sealed class ModuleOptionReader(MonicaApplication application, Type sourceModuleType)
    : IModuleOptionReader
{
    public TOptions Get<TModule, TOptions>()
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        return application.Modules.GetRequiredOptions<TModule, TOptions>(sourceModuleType);
    }

    public bool TryGet<TModule, TOptions>(out TOptions? options)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        return application.Modules.TryGetOrderedOptions<TModule, TOptions>(sourceModuleType, out options);
    }
}
