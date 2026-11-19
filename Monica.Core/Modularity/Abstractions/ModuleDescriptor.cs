namespace Monica.Core.Modularity.Abstractions;

/// <summary>
/// Declares the option-free graph contract of one module.
/// </summary>
/// <remarks>
/// Descriptors are valid only while the module graph is being compiled. They do not expose module options,
/// services, or host state, so graph shape cannot depend on configuration timing.
/// </remarks>
public sealed class ModuleDescriptor
{
    private readonly MonicaApplication _application;
    private readonly Type _ownerModuleType;

    internal ModuleDescriptor(MonicaApplication application, Type ownerModuleType)
    {
        _application = application;
        _ownerModuleType = ownerModuleType;
    }

    /// <summary>
    /// Adds a hard dependency. The dependency is included in the composition automatically.
    /// </summary>
    /// <typeparam name="TModule">The concrete dependency module.</typeparam>
    public void Require<TModule>() where TModule : MonicaModule, new()
    {
        _application.Modules.DescribeRequire<TModule>(_ownerModuleType);
    }

    /// <summary>
    /// Adds a hard dependency and contributes defaults applied before host option contributions.
    /// </summary>
    /// <typeparam name="TModule">The concrete dependency module.</typeparam>
    /// <typeparam name="TOptions">The dependency's option type.</typeparam>
    /// <param name="configure">The option defaults to contribute, or <see langword="null"/> to declare only the dependency.</param>
    /// <remarks>
    /// Calls compiled against the former optional callback can pass <see langword="null"/>.
    /// Use <see cref="Require{TModule}()"/> for graph-only declarations in source.
    /// </remarks>
    public void Require<TModule, TOptions>(Action<TOptions>? configure)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        var dependency = _application.Modules.DescribeRequire<TModule>(_ownerModuleType);
        if (configure is not null)
        {
            dependency.AddOptionContribution(_ownerModuleType, configure);
        }
    }

    /// <summary>
    /// Declares a feature that must be selected on a directly required dependency.
    /// </summary>
    /// <typeparam name="TModule">The directly required dependency module.</typeparam>
    /// <param name="featureName">The stable dependency feature name used in validation diagnostics.</param>
    /// <remarks>
    /// Declare the hard dependency with <see cref="Require{TModule}()"/> before requiring its feature.
    /// </remarks>
    public void RequireDependencyFeature<TModule>(string featureName) where TModule : MonicaModule, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        _application.Modules.DescribeRequireDependencyFeature(_ownerModuleType, typeof(TModule), featureName);
    }

    /// <summary>
    /// Adds an ordering relationship without including the target module.
    /// </summary>
    /// <typeparam name="TModule">The concrete module to order before this module when present.</typeparam>
    public void AfterIfPresent<TModule>() where TModule : MonicaModule, new()
    {
        _application.Modules.DescribeAfterIfPresent(_ownerModuleType, typeof(TModule));
    }

    /// <summary>
    /// Declares that an explicit registration feature must be selected before composition can proceed.
    /// </summary>
    /// <param name="featureName">The stable feature name used in validation diagnostics.</param>
    public void RequireFeature(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        _application.Modules.DescribeRequireFeature(_ownerModuleType, featureName);
    }
}
