using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Core.XmlDocumentation.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;
using Monica.ProjectUnits.Abstractions;
using Monica.ProjectUnits.Models;
using Monica.Tool.Extensions;

namespace Monica.ProjectUnits.Services.Support;

/// <summary>
/// Owns project-unit discovery, dependency analysis, and request-filter state for one Monica host.
/// </summary>
internal sealed class ProjectUnitCatalog : IProjectUnitCatalog, IRequestFilter
{
    private readonly Dictionary<string, ProjectUnit> _unitsByFullName = [];
    private readonly Dictionary<string, ProjectUnit?> _unitsByName = [];
    private readonly Dictionary<string, Type> _enumTypes = [];
    private readonly ConcurrentDictionary<string, byte> _disabledRequestUrls = new(StringComparer.Ordinal);
    private readonly ProjectUnitFactory _unitFactory;
    private int _documentationAttached;

    internal ProjectUnitCatalog(
        ModuleProjectUnitsOption options,
        ProjectUnitNamingOptions namingOptions,
        ILogger logger)
    {
        Options = options;
        NamingOptions = namingOptions;
        Logger = logger;
        _unitFactory = new ProjectUnitFactory(this);
    }

    internal ModuleProjectUnitsOption Options { get; }

    internal ProjectUnitNamingOptions NamingOptions { get; }

    internal ILogger Logger { get; }

    internal ProjectUnitDocumentationResolver Documentation { get; } = new(null);

    public IReadOnlyDictionary<string, Type> EnumTypes => _enumTypes;

    /// <summary>
    /// Creates and structurally completes one host catalog during module composition.
    /// </summary>
    /// <remarks>
    /// Composition-time completion is a timing contract: Monica.Configuration publishes definition
    /// metadata during host build, so reload-behavior inference must enrich the definition registry
    /// before the host service provider exists. XML documentation attachment is deferred to
    /// <see cref="AttachDocumentation"/> because the documentation service requires the built provider.
    /// </remarks>
    internal static ProjectUnitCatalog CreateAnalyzed(
        ModuleProjectUnitsOption options,
        ProjectUnitNamingOptions namingOptions,
        ILogger logger,
        IEnumerable<BusinessTypeShape> shapes,
        IConfigurationDefinitionRegistry? configurationDefinitionRegistry)
    {
        var catalog = new ProjectUnitCatalog(options, namingOptions, logger);
        catalog.Discover(shapes);
        catalog.ConnectUnits();
        ProjectUnitConfigurationReloadBehaviorEnricher.Enrich(
            configurationDefinitionRegistry,
            catalog,
            logger);
        return catalog;
    }

    /// <summary>
    /// Discovers project units from the compiler-owned structural shapes.
    /// </summary>
    internal void Discover(IEnumerable<BusinessTypeShape> shapes)
    {
        var shapesByType = shapes.DistinctBy(static shape => shape.Type)
            .ToDictionary(static shape => shape.Type);
        foreach (var shape in shapesByType.Values)
        {
            DiscoverProjectUnit(shape);
            DiscoverEnum(shape.Type);
        }

        // Assembly scans contain generic definitions, while handler contracts name closed payloads. Materialize
        // only constructions of definitions already in this host's discovery scope; excluded contracts stay absent.
        var referencedTypes = _unitsByFullName.Values.Select(static unit => unit switch
        {
            UnitDomainEventHandler handler => handler.EventType,
            UnitLocalEventHandler handler => handler.EventType,
            UnitApplicationService service => service.RequestType,
            _ => null
        }).OfType<Type>().Distinct().ToArray();
        foreach (var type in referencedTypes)
        {
            if (type.IsConstructedGenericType
                && !type.ContainsGenericParameters
                && !_unitsByFullName.ContainsKey(type.FullName!)
                && shapesByType.TryGetValue(type.GetGenericTypeDefinition(), out var definition))
            {
                DiscoverProjectUnit(definition.MakeGenericType(type.GetGenericArguments()));
            }
        }
    }

    internal void ConnectUnits()
    {
        foreach (var unit in _unitsByFullName.Values)
        {
            unit.DoingConnect();
        }
    }

    private void ApplyDocumentation(IXmlDocumentationService? documentationService)
    {
        var documentation = new ProjectUnitDocumentationResolver(documentationService);
        foreach (var unit in _unitsByFullName.Values)
        {
            unit.ApplyDocumentation(documentation);
        }
    }

    /// <summary>
    /// Attaches host-owned XML documentation once, after structural analysis is frozen.
    /// </summary>
    /// <param name="documentationService">
    /// The host documentation service, or <see langword="null"/> when the module does not parse unit details.
    /// </param>
    internal void AttachDocumentation(IXmlDocumentationService? documentationService)
    {
        if (Interlocked.Exchange(ref _documentationAttached, 1) == 1)
        {
            return;
        }

        ApplyDocumentation(documentationService);
    }

    internal ProjectUnit? ResolveConstructorDependency(Type parameterType, ProjectUnit dependentUnit)
    {
        if (FindByFullName(parameterType.FullName) is { } directDependency)
        {
            return directDependency;
        }

        if (!TryGetConfigurationUsage(parameterType, out var configurationType, out var usageType)
            || FindByFullName<UnitConfiguration>(configurationType.FullName) is not { } configurationUnit)
        {
            return null;
        }

        configurationUnit.RecordDependency(dependentUnit, usageType);
        return configurationUnit;
    }

    public IReadOnlyList<ProjectUnit> GetAllUnits()
    {
        return [.. _unitsByFullName.Values];
    }

    public IReadOnlyList<TProjectUnit> GetUnits<TProjectUnit>() where TProjectUnit : ProjectUnit
    {
        return [.. _unitsByFullName.Values.OfType<TProjectUnit>()];
    }

    public ProjectUnit? FindByFullName(string? typeFullName)
    {
        return !string.IsNullOrWhiteSpace(typeFullName)
               && _unitsByFullName.TryGetValue(typeFullName, out var unit)
            ? unit
            : null;
    }

    public ProjectUnit? FindByName(string? typeName)
    {
        return !string.IsNullOrWhiteSpace(typeName)
               && _unitsByName.TryGetValue(typeName, out var unit)
            ? unit
            : null;
    }

    public TProjectUnit? FindByFullName<TProjectUnit>(string? typeFullName) where TProjectUnit : ProjectUnit
    {
        return FindByFullName(typeFullName) as TProjectUnit;
    }

    public TProjectUnit? FindByName<TProjectUnit>(string? typeName) where TProjectUnit : ProjectUnit
    {
        return FindByName(typeName) as TProjectUnit;
    }

    public void Disable(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        _disabledRequestUrls.TryAdd(url, 0);
    }

    public void Enable(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        _disabledRequestUrls.TryRemove(url, out _);
    }

    public List<string> GetDisabledUrls()
    {
        return [.. _disabledRequestUrls.Keys];
    }

    internal bool IsRequestDisabled(string path)
    {
        return _disabledRequestUrls.ContainsKey(path);
    }

    private void DiscoverProjectUnit(BusinessTypeShape shape)
    {
        var unit = _unitFactory.Create(shape);
        if (unit is null)
        {
            return;
        }

        unit.PolishUnitInfo();
        _unitsByFullName.Add(unit.Key, unit);

        if (!_unitsByName.TryAdd(unit.Type.Name, unit))
        {
            // Equal short names are valid across domains. Never let a legacy alias select an arbitrary owner.
            _unitsByName[unit.Type.Name] = null;
        }
    }

    private void DiscoverEnum(Type type)
    {
        if (type.IsEnum && !type.IsGenericTypeDefinition && !type.ContainsGenericParameters)
        {
            _enumTypes.TryAdd(type.Name, type);
        }
    }

    private static bool TryGetConfigurationUsage(
        Type parameterType,
        out Type configurationType,
        out EConfigurationUsageType usageType)
    {
        configurationType = null!;
        usageType = EConfigurationUsageType.Unknown;

        if (!parameterType.IsInterface)
        {
            return false;
        }

        Type? optionsInterface;
        if (parameterType.IsImplementInterfaceGeneric(typeof(IOptionsMonitor<>), out optionsInterface))
        {
            usageType = EConfigurationUsageType.OnlineMonitor;
        }
        else if (parameterType.IsImplementInterfaceGeneric(typeof(IOptionsSnapshot<>), out optionsInterface))
        {
            usageType = EConfigurationUsageType.OnlineSnapshot;
        }
        else if (parameterType.IsImplementInterfaceGeneric(typeof(IOptions<>), out optionsInterface))
        {
            usageType = EConfigurationUsageType.Offline;
        }
        else
        {
            return false;
        }

        configurationType = optionsInterface.GetGenericArguments()[0];
        return true;
    }
}
