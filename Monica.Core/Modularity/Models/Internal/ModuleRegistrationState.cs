using Microsoft.Extensions.Logging;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Models;

namespace Monica.Core.Modularity.Models.Internal;

/// <summary>
/// Stores the mutable declaration draft for one module until composition is compiled.
/// </summary>
internal sealed class ModuleRegistrationState
{
    private readonly List<OptionContribution> _optionContributions = [];
    private readonly Dictionary<string, List<OwnedOptionContribution>> _profileContributions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _profiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _requiredFeatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _satisfiedFeatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keyedServiceKeys = new(StringComparer.Ordinal);
    private readonly List<ModuleServiceRequirement> _serviceRequirements = [];
    private readonly List<string> _webHostRequirementReasons = [];
    private readonly List<ModuleConfigurationRequest> _configurationRequests = [];
    private IModuleOptions? _moduleOption;
    private long _requestOrdinal;
    private Func<object>? _optionFactory;
    private bool _finalizingOptions;

    internal ModuleRegistrationState(MonicaApplication application, Type moduleType)
    {
        Application = application;
        ModuleType = moduleType;
    }

    internal MonicaApplication Application { get; }

    /// <summary>
    /// Gets the concrete module type.
    /// </summary>
    public Type ModuleType { get; }

    /// <summary>
    /// Gets the latest composition phase reached by the module.
    /// </summary>
    public ModulePhase ModulePhase { get; private set; }

    /// <summary>
    /// Gets the stable dependency-first execution order.
    /// </summary>
    public int Order { get; internal set; } = 1000;

    /// <summary>
    /// Gets the ordered lifecycle contributions owned by this module.
    /// </summary>
    internal IReadOnlyList<ModuleConfigurationRequest> ConfigurationRequests => _configurationRequests;

    /// <summary>
    /// Gets the primary module option type.
    /// </summary>
    public Type ModuleOptionType { get; private set; } = null!;

    /// <summary>
    /// Gets the finalized primary module option.
    /// </summary>
    public IModuleOptions ModuleOption => _moduleOption
        ?? throw new InvalidOperationException($"Module {ModuleType.Name} options have not been finalized.");

    /// <summary>
    /// Gets keyed service identities published by this module.
    /// </summary>
    internal IReadOnlySet<string> KeyedServiceKeys => _keyedServiceKeys;

    /// <summary>
    /// Gets finalized service identities consumed by this module.
    /// </summary>
    internal IReadOnlyList<ModuleServiceRequirement> ServiceRequirements => _serviceRequirements;

    /// <summary>
    /// Gets the single host-owned module strategy instance.
    /// </summary>
    public MonicaModule ModuleSingleton { get; private set; } = null!;

    internal string? DisabledReason { get; private set; }

    internal bool RequiresWebHost =>
        ModuleSingleton.RequiresWebHost || _webHostRequirementReasons.Count != 0;

    internal string? WebHostRequirementReason
    {
        get
        {
            var declaredReasons = string.Join("; ", _webHostRequirementReasons);
            if (!ModuleSingleton.RequiresWebHost)
            {
                return declaredReasons.Length == 0 ? null : declaredReasons;
            }

            const string intrinsicReason =
                "The module declares an intrinsic ASP.NET Core web lifecycle requirement.";
            return declaredReasons.Length == 0
                ? intrinsicReason
                : $"{intrinsicReason} {declaredReasons}";
        }
    }

    internal bool IsFinalized => _moduleOption is not null;

    internal void Initialize<TModule, TOptions>()
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        if (_optionFactory is not null)
        {
            if (ModuleOptionType != typeof(TOptions) || ModuleType != typeof(TModule))
            {
                throw new InvalidOperationException(
                    $"Module {ModuleType.Name} was registered with conflicting option metadata.");
            }

            return;
        }

        ModuleOptionType = typeof(TOptions);
        ModuleSingleton = new TModule();
        _optionFactory = static () => new TOptions();
    }

    internal void AddOptionContribution<TOptions>(Type? configuredBy, Action<TOptions> configure, Type? owner = null)
        where TOptions : class, IModuleOptions, new()
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureNotFinalized();
        _optionContributions.Add(new OptionContribution(
            IsHostContribution: configuredBy is null,
            Ordinal: _optionContributions.Count,
            Apply: option => configure((TOptions)option), Owner: owner));
    }

    internal void AddProfileContribution<TOptions>(string name, Action<TOptions> configure, Type? owner)
        where TOptions : class, IModuleOptions, new()
    {
        EnsureNotFinalized();
        if (!_profileContributions.TryGetValue(name, out var contributions))
        {
            contributions = [];
            _profileContributions.Add(name, contributions);
        }

        contributions.Add(new OwnedOptionContribution(option => configure((TOptions)option), owner));
    }

    internal TOptions GetProfile<TOptions>(string name)
        where TOptions : class, IModuleOptions, new()
    {
        if (!IsFinalized)
        {
            throw new InvalidOperationException(
                $"Named option profile '{name}' for {ModuleType.Name} has not been finalized.");
        }

        return _profiles.TryGetValue(name, out var profile)
            ? (TOptions)profile
            : throw new KeyNotFoundException(
                $"Named option profile '{name}' was not declared for {ModuleType.Name}.");
    }

    internal object GetOptionOrDefault(string? profileName)
    {
        if (!IsFinalized)
        {
            throw new InvalidOperationException(
                $"Module {ModuleType.Name} options have not been finalized.");
        }

        if (profileName is null)
        {
            return ModuleOption;
        }

        return _profiles.TryGetValue(profileName, out var profile)
            ? profile
            : throw new KeyNotFoundException(
                $"Named option profile '{profileName}' was not declared for {ModuleType.Name}.");
    }

    internal bool TryGetProfile(string profileName, out object profile)
    {
        if (!IsFinalized)
        {
            throw new InvalidOperationException(
                $"Named option profile '{profileName}' for {ModuleType.Name} has not been finalized.");
        }

        if (_profiles.TryGetValue(profileName, out var configuredProfile))
        {
            profile = configuredProfile;
            return true;
        }

        profile = null!;
        return false;
    }

    internal void RequireFeature(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        EnsureNotFinalized();
        _requiredFeatures.Add(featureName);
    }

    internal void SatisfyFeature(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        EnsureNotFinalized();
        _satisfiedFeatures.Add(featureName);
    }

    internal void AddKeyedServiceKey(string serviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        EnsureNotFinalized();
        _keyedServiceKeys.Add(serviceKey);
    }

    internal void RequireService(Type serviceType, bool isKeyed, object? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (isKeyed)
        {
            ArgumentNullException.ThrowIfNull(serviceKey);
        }
        else if (serviceKey is not null)
        {
            throw new ArgumentException("An unkeyed service requirement cannot have a service key.", nameof(serviceKey));
        }

        var requirement = new ModuleServiceRequirement(serviceType, isKeyed, serviceKey);
        if (!_serviceRequirements.Contains(requirement))
        {
            _serviceRequirements.Add(requirement);
        }
    }

    internal void MarkDisabled(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureNotFinalized();
        DisabledReason ??= reason;
        ModulePhase = ModulePhase.Disabled;
    }

    internal void RequireWebHost(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureNotFinalized();
        if (!_webHostRequirementReasons.Contains(reason, StringComparer.Ordinal))
        {
            _webHostRequirementReasons.Add(reason);
        }
    }

    internal void FinalizeOptions()
    {
        if (_optionFactory is null)
        {
            throw new InvalidOperationException($"Module {ModuleType.Name} has no option factory.");
        }

        if (IsFinalized || _finalizingOptions)
        {
            throw new InvalidOperationException($"Module {ModuleType.Name} was already finalized.");
        }

        _finalizingOptions = true;
        var option = CreateBoundOption();
        foreach (var contribution in _optionContributions
                     .OrderBy(static contribution => contribution.IsHostContribution)
                     .ThenBy(static contribution => contribution.Ordinal))
        {
            Application.Composition.RunOwned(contribution.Owner, () => contribution.Apply(option));
        }

        _moduleOption = (IModuleOptions)option;
        foreach (var (name, contributions) in _profileContributions)
        {
            var profile = CreateBoundOption();
            foreach (var contribution in contributions)
            {
                Application.Composition.RunOwned(contribution.Owner, () => contribution.Apply(profile));
            }

            _profiles.Add(name, profile);
        }

        ModuleSingleton.BindOptions(Application, option);
        ModuleSingleton.ValidateFinalOptions(option, profileName: null);
        foreach (var (name, profile) in _profiles)
        {
            ModuleSingleton.ValidateFinalOptions(profile, name);
        }

        ModuleSingleton.AttachLifecycle(this);
        _finalizingOptions = false;
    }

    internal void AddLifecycleRequest(
        ModulePhase phase,
        int order,
        Action<ModuleConfigurationContext> configure)
    {
        AddRequest(phase, ModuleCallbackKind.Lifecycle, order, configure);
    }

    internal void AddApplicationBuilderLifecycleRequest(
        ModuleWebStage stage,
        int order,
        Action<ModuleConfigurationContext> configure)
    {
        AddApplicationBuilderRequest(stage, ModuleCallbackKind.Lifecycle, order, configure);
    }

    internal void AddContributionRequest(
        ModulePhase phase,
        int order,
        Action<ModuleConfigurationContext> configure,
        Type? owner)
    {
        AddRequest(phase, ModuleCallbackKind.RegistrationContribution, order, configure, owner: owner);
    }

    internal void AddApplicationBuilderContributionRequest(
        ModuleWebStage stage,
        Action<ModuleConfigurationContext> configure,
        Type? owner)
    {
        AddApplicationBuilderRequest(
            stage,
            ModuleCallbackKind.RegistrationContribution,
            order: 0,
            configure,
            owner);
    }

    private void AddApplicationBuilderRequest(
        ModuleWebStage stage,
        ModuleCallbackKind kind,
        int order,
        Action<ModuleConfigurationContext> configure,
        Type? owner = null)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown web lifecycle stage.");
        }

        AddRequest(
            ModulePhase.ConfigureApplicationBuilder,
            kind,
            order,
            configure,
            stage,
            owner);
    }

    private void AddRequest(
        ModulePhase phase,
        ModuleCallbackKind kind,
        int order,
        Action<ModuleConfigurationContext> configure,
        ModuleWebStage? webStage = null,
        Type? owner = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var ordinal = _requestOrdinal++;
        _configurationRequests.Add(new ModuleConfigurationRequest(
            phase,
            kind,
            order,
            ordinal,
            configure,
            webStage,
            owner));
    }

    internal IReadOnlyList<string> GetMissingRequiredFeatures()
    {
        return _requiredFeatures
            .Where(feature => !_satisfiedFeatures.Contains(feature))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal IReadOnlyList<string> GetUnexpectedSatisfiedFeatures()
    {
        return _satisfiedFeatures
            .Where(feature => !_requiredFeatures.Contains(feature))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Returns lifecycle callbacks in deterministic order.
    /// </summary>
    internal IEnumerable<ModuleConfigurationRequest> GetOrderedRequests(
        IEnumerable<ModuleConfigurationRequest> requests)
    {
        return requests.OrderBy(static request => request.Order)
            .ThenBy(static request => request.Ordinal);
    }

    internal void StartModulePhase(
        ModulePhase phase,
        ModuleCallbackKind kind = ModuleCallbackKind.Lifecycle,
        string? workItemId = null)
    {
        Application.Modules.StartModulePhase(this, phase, kind, workItemId);
    }

    /// <summary>Updates the visible phase while the registry diagnostics lock is held.</summary>
    internal void SetModulePhase(ModulePhase phase)
    {
        ModulePhase = phase;
    }

    internal void EndModulePhase(ModulePhase phase)
    {
        Application.Profiling.StopModulePhase(ModuleType, phase);
    }

    private object CreateBoundOption()
    {
        var option = _optionFactory!();
        if (option is IModuleOptionsContext context)
        {
            context.Bind(Application);
        }

        return option;
    }

    private void EnsureNotFinalized()
    {
        if (IsFinalized || _finalizingOptions)
        {
            throw new InvalidOperationException(
                $"Module {ModuleType.Name} is already compiled and cannot be changed.");
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{ModulePhase} - {ModuleType.Name}";
    }

    private sealed record OptionContribution(
        bool IsHostContribution,
        int Ordinal,
        Action<object> Apply,
        Type? Owner);

    private sealed record OwnedOptionContribution(Action<object> Apply, Type? Owner);
}
