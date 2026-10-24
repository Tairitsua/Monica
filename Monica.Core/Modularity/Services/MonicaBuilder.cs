using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.TypeDiscovery.Models;

namespace Monica.Core.Modularity.Services;

/// <summary>
/// Default host-bound Monica composition builder.
/// </summary>
internal sealed class MonicaBuilder(IHostApplicationBuilder hostBuilder, MonicaApplication application)
    : IMonicaBuilder
{
    private bool _isCompleted;
    private bool _isCompleting;

    /// <inheritdoc />
    public MonicaCompositionState Composition => application.Composition;

    /// <inheritdoc />
    public IMonicaBuilder ConfigureApplication(Action<MonicaApplicationOptions> configure)
    {
        EnsureCompositionIsOpen();
        ArgumentNullException.ThrowIfNull(configure);
        configure(application.ApplicationConfiguration);
        return this;
    }

    /// <inheritdoc />
    public IMonicaBuilder ConfigureModuleSystem(Action<MonicaModuleSystemOptions> configure)
    {
        EnsureCompositionIsOpen();
        ArgumentNullException.ThrowIfNull(configure);
        configure(application.ModuleSystemConfiguration);
        return this;
    }

    /// <inheritdoc />
    public IMonicaBuilder ConfigureTypeDiscovery(Action<TypeFinderOptions>? configure = null)
    {
        EnsureCompositionIsOpen();
        application.ConfigureTypeDiscovery(configure);
        return this;
    }

    /// <inheritdoc />
    public ModuleRegistration<TModule, TModuleOption> AddModule<TModule, TModuleOption>(
        Action<TModuleOption>? configure = null)
        where TModuleOption : ModuleOptions<TModule>, new()
        where TModule : MonicaModule<TModuleOption>, new()
    {
        EnsureCompositionIsOpen();
        return new ModuleRegistration<TModule, TModuleOption>(application, configuredBy: null)
            .Configure(configure);
    }

    /// <summary>
    /// Closes graph composition and applies the validated module lifecycle to the host.
    /// </summary>
    internal void Complete()
    {
        EnsureCompositionIsOpen();
        if (_isCompleting) throw new InvalidOperationException("Application composition cannot execute recursively.");
        _isCompleting = true;
        try
        {
            application.ModuleSystemConfiguration.Validate();
            application.ModuleSystemConfiguration.ValidateEnvironment(hostBuilder.Environment);
            application.Modules.RegisterServices(hostBuilder, this);
        }
        finally
        {
            _isCompleted = true;
            _isCompleting = false;
        }
    }

    private void EnsureCompositionIsOpen()
    {
        if (_isCompleted)
        {
            throw new InvalidOperationException(
                "The Monica composition callback has completed and can no longer be modified.");
        }
    }
}
