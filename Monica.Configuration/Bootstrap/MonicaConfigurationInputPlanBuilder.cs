using System.Collections.Immutable;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Bootstrap;

/// <summary>
/// Collects one bounded declaration for an immutable <see cref="MonicaConfigurationInputPlan"/>.
/// </summary>
public sealed class MonicaConfigurationInputPlanBuilder
{
    private readonly List<ManagedJsonConfigurationSourceRegistration> _managedJsonSources = [];
    private IMonicaConfigurationStoreComposition? _storeComposition;
    private ConfigurationSectionPathConvention _sectionPathConvention =
        ConfigurationSectionPathConvention.ShortTypeName;
    private bool _sectionPathConventionConfigured;
    private bool _isSealed;

    internal MonicaConfigurationInputPlanBuilder()
    {
    }

    /// <summary>
    /// Selects the single store composition for runtime managed configuration.
    /// </summary>
    /// <param name="storeComposition">The runtime store composition.</param>
    /// <returns>This declaration builder.</returns>
    public MonicaConfigurationInputPlanBuilder UseConfigurationStore(
        IMonicaConfigurationStoreComposition storeComposition)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(storeComposition);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeComposition.Name);

        if (_storeComposition is not null)
        {
            throw new InvalidOperationException(
                $"Configuration input plan already uses store '{_storeComposition.Name}' and cannot also use '{storeComposition.Name}'.");
        }

        _storeComposition = storeComposition;
        return this;
    }

    /// <summary>
    /// Selects the section-path convention for Managed definitions without explicit section paths.
    /// </summary>
    /// <param name="convention">The convention to use for types without explicit section paths.</param>
    /// <returns>This declaration builder.</returns>
    public MonicaConfigurationInputPlanBuilder UseSectionPathConvention(
        ConfigurationSectionPathConvention convention)
    {
        EnsureOpen();
        if (!Enum.IsDefined(convention))
        {
            throw new ArgumentOutOfRangeException(nameof(convention), convention, "Unknown section-path convention.");
        }

        if (_sectionPathConventionConfigured)
        {
            throw new InvalidOperationException("The Configuration section-path convention was already declared.");
        }

        _sectionPathConvention = convention;
        _sectionPathConventionConfigured = true;
        return this;
    }

    /// <summary>
    /// Appends a managed JSON source at the highest current source priority.
    /// </summary>
    /// <param name="registration">The immutable source registration.</param>
    /// <returns>This declaration builder.</returns>
    public MonicaConfigurationInputPlanBuilder AddManagedJsonSource(
        ManagedJsonConfigurationSourceRegistration registration)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.DisplayName);

        if (_managedJsonSources.Any(source =>
                string.Equals(source.Path, registration.Path, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Managed JSON source path '{registration.Path}' is already declared.");
        }

        _managedJsonSources.Add(registration);
        return this;
    }

    /// <summary>
    /// Appends a managed JSON file at the highest current source priority.
    /// </summary>
    /// <param name="path">The JSON file path resolved from the host content root.</param>
    /// <param name="optional">Whether the file may be absent.</param>
    /// <param name="reloadOnChange">
    /// Whether the runtime Microsoft configuration provider watches the file for changes.
    /// </param>
    /// <param name="configure">Optional operator-facing source metadata configuration.</param>
    /// <returns>This declaration builder.</returns>
    public MonicaConfigurationInputPlanBuilder AddManagedJsonFile(
        string path,
        bool optional = true,
        bool reloadOnChange = true,
        Action<ManagedJsonConfigurationSourceOptions>? configure = null)
    {
        return AddManagedJsonSource(
            ManagedJsonConfigurationSourceRegistration.Create(
                path,
                optional,
                reloadOnChange,
                configure));
    }

    internal MonicaConfigurationInputPlan Build()
    {
        EnsureOpen();
        _isSealed = true;

        var storeComposition = _storeComposition
            ?? throw new InvalidOperationException(
                "Configuration input plan must select exactly one store composition.");
        return new MonicaConfigurationInputPlan(
            storeComposition,
            _managedJsonSources.ToImmutableArray(),
            _sectionPathConvention);
    }

    private void EnsureOpen()
    {
        if (_isSealed)
        {
            throw new InvalidOperationException("The Configuration input-plan declaration is sealed.");
        }
    }
}
