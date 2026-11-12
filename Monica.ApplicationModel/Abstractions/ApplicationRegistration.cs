using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;

namespace Monica.ApplicationModel.Abstractions;

/// <summary>Declares passive services and selected integrations for one included domain or role assembly.</summary>
/// <remarks>
/// Name one concrete registration on the owning assembly attribute. It must belong to that assembly and expose
/// a public parameterless constructor. Callbacks run once before module declarations close; they may register
/// descriptors and modules, but must not resolve services, open connections, or start background activity.
/// </remarks>
public abstract class ApplicationRegistration
{
    /// <summary>Registers passive services whenever this implementation assembly is included in the host.</summary>
    public virtual void ConfigurePassive(ApplicationRegistrationContext context) { }

    /// <summary>Registers integrations when the role, or the domain's BackgroundRole, is assigned to this AppId.</summary>
    public virtual void ConfigureActive(ApplicationRegistrationContext context) { }
}

/// <summary>Provides frozen startup inputs and host-owned declaration surfaces to an application registration.</summary>
/// <remarks>Identity, deployment placement, discovery, and configuration-source selection are already frozen.</remarks>
public sealed class ApplicationRegistrationContext : IMonicaCompositionContext
{
    internal ApplicationRegistrationContext(IMonicaBuilder monica, IHostApplicationBuilder hostBuilder, bool isActive, bool autonomousWork = true)
    {
        Monica = monica;
        Services = hostBuilder.Services;
        Environment = hostBuilder.Environment;
        IsActive = isActive;
        IsAutonomous = isActive && autonomousWork;
    }

    /// <summary>Gets the open Monica builder for module declarations owned by this callback.</summary>
    public IMonicaBuilder Monica { get; }

    /// <summary>Gets the host service collection for descriptor registration without resolving services.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Gets the immutable configuration captured before the host's AddMonica callback.</summary>
    public IConfiguration Configuration => Composition.StartupConfiguration;

    /// <summary>Gets the host environment for this registration.</summary>
    public IHostEnvironment Environment { get; }

    /// <summary>Gets whether the owning role or domain BackgroundRole is assigned to this AppId.</summary>
    public bool IsActive { get; }

    /// <summary>Gets whether the selected integration may participate autonomously in this host.</summary>
    public bool IsAutonomous { get; }

    /// <inheritdoc />
    public MonicaCompositionState Composition => Monica.Composition;
}
