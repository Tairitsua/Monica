using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Configuration.Annotations;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Binding;
using Monica.Configuration.Bootstrap;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Facades;
using Monica.Configuration.Metrics;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.TypeDiscovery.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions for the Monica.Configuration module.
/// </summary>
public static class ModuleConfigurationBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the schema-first Monica configuration module.
        /// </summary>
        /// <param name="action">Optional module option configuration.</param>
        /// <returns>The host-bound configuration module registration.</returns>
        public ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> AddConfiguration(Action<ModuleConfigurationOption>? action = null)
        {
            return builder.AddModule<ModuleConfiguration, ModuleConfigurationOption>(action);
        }

        /// <summary>
        /// Registers the schema-first Monica configuration module from one immutable input plan.
        /// </summary>
        /// <param name="inputPlan">
        /// The store, section-path convention, and ordered managed JSON sources shared by startup and runtime.
        /// </param>
        /// <param name="action">Optional remaining module option configuration.</param>
        /// <returns>The host-bound configuration module registration.</returns>
        /// <remarks>
        /// Applying the plan records runtime composition only; it does not create a store or perform I/O. The plan's
        /// section-path convention is authoritative and conflicting option configuration fails during finalization.
        /// </remarks>
        public ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> AddConfiguration(
            MonicaConfigurationInputPlan inputPlan,
            Action<ModuleConfigurationOption>? action = null)
        {
            ArgumentNullException.ThrowIfNull(inputPlan);

            var registration = builder.AddModule<ModuleConfiguration, ModuleConfigurationOption>(options =>
            {
                if (options.InputPlanApplied)
                {
                    throw new InvalidOperationException(
                        "Only one Monica Configuration input plan can be applied to a host.");
                }

                options.InputPlanApplied = true;
                options.InputPlanSectionPathConvention = inputPlan.SectionPathConvention;
                options.DefaultSectionPathConvention = inputPlan.SectionPathConvention;
                action?.Invoke(options);
            });
            inputPlan.ConfigureRuntime(registration);
            return registration;
        }
    }
}

/// <summary>
/// Monica configuration module.
/// </summary>
public sealed class ModuleConfiguration : MonicaModule<ModuleConfigurationOption>, IWebModule
{
    private static readonly MethodInfo ADD_OPTIONS_METHOD = GetRequiredGenericMethod(
        typeof(OptionsServiceCollectionExtensions),
        nameof(OptionsServiceCollectionExtensions.AddOptions),
        [typeof(IServiceCollection)]);

    private static readonly MethodInfo BIND_OPTIONS_METHOD = GetRequiredGenericMethod(
        typeof(MonicaConfigurationBinder),
        nameof(MonicaConfigurationBinder.BindOptions),
        [typeof(OptionsBuilder<>), typeof(IConfiguration), typeof(string), typeof(string)]);

    private readonly ConfigurationDefinitionRegistry _definitionRegistry = new();
    private readonly ConfigurationLocalDefinitionRegistry _localDefinitionRegistry = new();
    private readonly ConfigurationRuntimeContext _runtimeContext = new();
    private readonly ConfigurationSchemaHasher _schemaHasher = new();
    private ConfigurationDefinitionScanner? _definitionScanner;
    private readonly MonicaConfigurationProviderAccessor _providerAccessor = new();
    private ConfigurationDefinitionAnalysis _definitionAnalysis = ConfigurationDefinitionAnalysis.Empty;
    private IServiceCollection? _services;

    /// <inheritdoc />
    public override void ValidateOptions(ModuleConfigurationOption options, string? profileName)
    {
        if (options.InputPlanSectionPathConvention is { } inputPlanConvention
            && options.DefaultSectionPathConvention != inputPlanConvention)
        {
            throw new InvalidOperationException(
                $"{nameof(ModuleConfigurationOption.DefaultSectionPathConvention)} must remain '{inputPlanConvention}' because the Configuration input plan uses that convention for startup loading.");
        }
    }

    /// <inheritdoc />
    public override void ConfigureBuilder(ModuleBuilderContext<ModuleConfigurationOption> context)
    {
        var builder = context.HostApplicationBuilder;
        ShapeAwareJsonConfigurationExtensions.PreserveJsonShapes(builder.Configuration);
        _definitionScanner = new ConfigurationDefinitionScanner(
            _schemaHasher,
            Option.DefaultSectionPathConvention);
        _runtimeContext.Capture(builder.Configuration);
        // This appends Monica's effective-value projection after the host's bootstrap providers.
        // If callers add more Microsoft configuration providers later, their ordering relative to Monica
        // should become an explicit module option or guide method instead of relying on call order.
        builder.Configuration.Add(new MonicaConfigurationSource(_providerAccessor));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleConfigurationOption> context)
    {
        var services = context.Services;
        if (!Enum.IsDefined(Option.RuntimeValidationBehavior))
        {
            throw new InvalidOperationException(
                $"Unsupported {nameof(ConfigurationRuntimeValidationBehavior)} value '{Option.RuntimeValidationBehavior}'.");
        }

        _services = services;
        services.TryAddSingleton<IConfigurationDefinitionRegistry>(_definitionRegistry);
        services.TryAddSingleton(_localDefinitionRegistry);
        services.TryAddSingleton(new ConfigurationValidationPolicy(Option.RuntimeValidationBehavior));
        services.TryAddSingleton<ConfigurationObjectMaterializer>();
        services.TryAddSingleton<ConfigurationObjectValidationWalker>();
        services.TryAddSingleton<ConfigurationOptionsValidationDiagnostics>();
        services.TryAddSingleton<IConfigurationOptionsValidationDiagnostics>(provider =>
            provider.GetRequiredService<ConfigurationOptionsValidationDiagnostics>());
        services.TryAddSingleton<IConfigurationOptionsInstanceValidationService, ConfigurationOptionsInstanceValidationService>();
        services.TryAddSingleton<IConfigurationEffectiveValueReader>(serviceProvider =>
            serviceProvider.GetRequiredService<IConfigurationEffectiveValueStore>());
        services.TryAddSingleton<ConfigurationDefinitionResolver>();
        services.TryAddSingleton<ConfigurationEffectiveStateReader>();
        services.TryAddSingleton<IConfigurationStoreStateTracker, ConfigurationStoreStateTracker>();
        services.TryAddSingleton<IConfigurationHistoryService, ConfigurationHistoryService>();
        services.TryAddSingleton<
            IConfigurationDefinitionChangeImpactService,
            ConfigurationDefinitionChangeImpactService>();
        services.TryAddSingleton<IConfigurationMutationBatchStore, SequentialConfigurationMutationBatchStore>();
        services.TryAddSingleton<IConfigurationMutationGroupService, ConfigurationMutationGroupService>();
        services.TryAddSingleton<ConfigurationRuntimeSnapshotLock>();
        services.TryAddSingleton<IConfigurationMutationGroupApplyService, ConfigurationMutationGroupApplyService>();
        services.TryAddSingleton<IConfigurationRollbackService, ConfigurationRollbackService>();
        services.TryAddSingleton<IConfigurationUnifiedVersionService, ConfigurationUnifiedVersionService>();
        services.TryAddSingleton<IConfigurationUnifiedVersionCoordinator, ConfigurationUnifiedVersionCoordinator>();
        services.TryAddSingleton<ConfigurationEffectiveSnapshotReader>();
        services.TryAddSingleton<ConfigurationUnifiedVersionSnapshotFactory>();
        services.TryAddSingleton<ConfigurationUnifiedVersionRollbackPreviewFactory>();
        services.TryAddSingleton<ConfigurationRollbackPersistencePlanner>();
        services.TryAddSingleton<IConfigurationReloadCoordinator, ConfigurationProviderReloadCoordinator>();
        services.TryAddSingleton<IConfigurationReloadSignalReceiver, ConfigurationReloadSignalReceiver>();
        services.TryAddSingleton<ConfigurationReloadNotificationDispatcher>();
        services.TryAddSingleton<IConfigurationReloadBroadcastService, ConfigurationReloadBroadcastService>();
        services.TryAddSingleton(_schemaHasher);
        services.TryAddSingleton<ConfigurationStoredValueCodec>();
        services.TryAddSingleton<ConfigurationValueValidationEngine>();
        services.TryAddSingleton<ConfigurationValidationCoordinator>();
        services.TryAddSingleton<IConfigurationCandidateValidationService, ConfigurationCandidateValidationService>();
        services.TryAddSingleton<ConfigurationMutationPlanner>();
        services.TryAddSingleton<ConfigurationPathProjector>();
        services.TryAddSingleton<ConfigurationEffectiveValuePatchEngine>();
        services.TryAddSingleton<ConfigurationEffectiveValueDocumentEditor>();
        services.TryAddSingleton(provider => new ConfigurationEffectiveValueSeedFactory(
            _runtimeContext, _localDefinitionRegistry, provider.GetRequiredService<ConfigurationObjectMaterializer>()));
        services.TryAddSingleton<IConfigurationSourceInspector, ConfigurationSourceInspector>();
        services.TryAddSingleton<IConfigurationRuntimeValidationService, ConfigurationRuntimeValidationService>();
        services.TryAddSingleton<IConfigurationRuntimeReloadService, ConfigurationRuntimeReloadService>();
        services.TryAddSingleton<IConfigurationJsonFileSourceWriter, ConfigurationJsonFileSourceWriter>();
        services.TryAddSingleton(_runtimeContext);
        services.TryAddSingleton(_providerAccessor);
        services.TryAddSingleton<ConfigurationMetricsRecorder>();
        services.TryAddSingleton<ConfigurationPublisherIdentityProvider>();
        services.TryAddSingleton<MonicaConfigurationProviderActivationCoordinator>();
        services.AddHostedService<MonicaConfigurationProviderActivationHostedService>();
        services.TryAddSingleton<ConfigurationFacade>();
    }

    /// <inheritdoc />
    public override void ConfigureApplicationBuilder(WebModuleContext<ModuleConfigurationOption> context)
    {
        var app = context.ApplicationBuilder;
        var activationCoordinator = app.ApplicationServices.GetRequiredService<MonicaConfigurationProviderActivationCoordinator>();
        activationCoordinator.ActivateAsync(CancellationToken.None).GetAwaiter().GetResult();

        var validationService = app.ApplicationServices.GetRequiredService<IConfigurationRuntimeValidationService>();
        var metricsRecorder = app.ApplicationServices.GetRequiredService<ConfigurationMetricsRecorder>();
        var report = metricsRecorder.MeasureStartupStage(
            ConfigurationStartupStage.RuntimeValidation,
            () => validationService.GetReport());
        if (report.IsValid)
        {
            return;
        }

        if (Option.RuntimeValidationBehavior == ConfigurationRuntimeValidationBehavior.FailFast)
        {
            throw new ConfigurationRuntimeValidationException(report);
        }

        Logger.LogWarning(
            "{ConfigurationRuntimeValidationDiagnostic}",
            ConfigurationRuntimeValidationMessageFormatter.FormatDiagnosticReport(report));
    }

    /// <inheritdoc />
    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleConfigurationOption> discovery)
    {
        discovery.Match(
            TypeQuery.ConcreteClass.HasAttribute<ConfigurationAttribute>(),
            (_, matches) =>
            {
                if (matches.Count == 0)
                {
                    return;
                }

                var typesToAnalyze = matches.Select(static match => match.Type).ToArray();
                ScheduleStartupWork(
                    "build-configuration-definitions",
                    () => BuildDefinitions(typesToAnalyze),
                    CommitDefinitions,
                    ModuleStartupWorkBarrier.BeforePostConfigureServices);
            });
    }

    private void BuildDefinitions(IReadOnlyList<Type> optionsTypes)
    {
        var scanner = _definitionScanner
            ?? throw new InvalidOperationException("Configuration definition discovery ran before host-builder configuration.");
        var analysis = ConfigurationDefinitionAnalysis.Create(scanner, optionsTypes);
        var conflicts = ValidateDefinitions(analysis.Registrations);
        Volatile.Write(ref _definitionAnalysis, analysis.WithSectionPathConflicts(conflicts));
    }

    private void CommitDefinitions()
    {
        var services = _services
            ?? throw new InvalidOperationException($"{nameof(ModuleConfiguration)} services have not been configured.");
        var analysis = Volatile.Read(ref _definitionAnalysis);

        foreach (var registration in analysis.Registrations)
        {
            RegisterOptionsBinding(
                services,
                registration.OptionsType,
                registration.Definition.SectionPath,
                registration.Definition.DefinitionKey);
        }

        _definitionRegistry.RegisterRange(
            analysis.Registrations.Select(static registration => registration.Definition));
        _localDefinitionRegistry.RegisterRange(analysis.Registrations);

        foreach (var conflict in analysis.SectionPathConflicts)
        {
            Logger.LogWarning(
                "Duplicate Monica configuration section path '{SectionPath}' is used by definitions '{ExistingDefinitionKey}' and '{NewDefinitionKey}'.",
                conflict.Existing.SectionPath,
                conflict.Existing.DefinitionKey,
                conflict.Duplicate.DefinitionKey);
        }
    }

    private IReadOnlyList<ConfigurationSectionPathConflict> ValidateDefinitions(
        IReadOnlyList<ConfigurationDefinitionRegistration> registrations)
    {
        var duplicateKey = registrations
            .GroupBy(static registration => registration.Definition.DefinitionKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Skip(1).Any());
        if (duplicateKey is not null)
        {
            var types = string.Join(
                ", ",
                duplicateKey.Select(static registration => registration.OptionsType.FullName)
                    .Order(StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"Configuration definition key '{duplicateKey.Key}' is declared by multiple options types: {types}.");
        }

        var definitions = _definitionRegistry.GetAll()
            .Concat(registrations.Select(static registration => registration.Definition))
            .OrderBy(static definition => definition.DefinitionKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var conflicts = new List<ConfigurationSectionPathConflict>();
        foreach (var sectionGroup in definitions.GroupBy(
                     static definition => definition.SectionPath,
                     StringComparer.OrdinalIgnoreCase))
        {
            var distinctDefinitions = sectionGroup
                .GroupBy(static definition => definition.DefinitionKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(static definition => definition.DefinitionKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinctDefinitions.Length < 2)
            {
                continue;
            }

            foreach (var duplicate in distinctDefinitions.Skip(1))
            {
                var conflict = new ConfigurationSectionPathConflict(distinctDefinitions[0], duplicate);
                if (Option.DuplicateSectionPathBehavior == ConfigurationDuplicateSectionPathBehavior.FailFast)
                {
                    throw new InvalidOperationException(BuildDuplicateSectionPathMessage(conflict));
                }

                conflicts.Add(conflict);
            }
        }

        return conflicts;
    }

    private static string BuildDuplicateSectionPathMessage(ConfigurationSectionPathConflict conflict)
    {
        return
            $"Configuration section path '{conflict.Existing.SectionPath}' is used by both '{conflict.Existing.DefinitionKey}' and '{conflict.Duplicate.DefinitionKey}'. " +
            $"Set an explicit {nameof(ConfigurationAttribute.SectionPath)}, change {nameof(ModuleConfigurationOption.DefaultSectionPathConvention)}, " +
            $"or set {nameof(ModuleConfigurationOption.DuplicateSectionPathBehavior)} to {nameof(ConfigurationDuplicateSectionPathBehavior.Warning)}.";
    }

    private void RegisterOptionsBinding(
        IServiceCollection services,
        Type optionsType,
        string sectionPath,
        string definitionKey)
    {
        var optionsBuilder = ADD_OPTIONS_METHOD.MakeGenericMethod(optionsType).Invoke(null, [services])
            ?? throw new InvalidOperationException($"Failed to create OptionsBuilder for '{optionsType.FullName}'.");

        BIND_OPTIONS_METHOD.MakeGenericMethod(optionsType).Invoke(null,
            [optionsBuilder, _runtimeContext.Configuration, sectionPath, definitionKey]);
        RegisterOptionsValidator(services, optionsType, definitionKey, Option.RuntimeValidationBehavior);
    }

    private static void RegisterOptionsValidator(
        IServiceCollection services,
        Type optionsType,
        string definitionKey,
        ConfigurationRuntimeValidationBehavior behavior)
    {
        var serviceType = typeof(IValidateOptions<>).MakeGenericType(optionsType);
        var validatorType = typeof(MonicaConfigurationOptionsValidator<>).MakeGenericType(optionsType);
        services.AddSingleton(serviceType, provider => ActivatorUtilities.CreateInstance(provider, validatorType, behavior, definitionKey));
    }

    private static MethodInfo GetRequiredGenericMethod(Type extensionType, string methodName, IReadOnlyList<Type> parameterTypeDefinitions)
    {
        return extensionType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(method => MatchesGenericMethod(method, methodName, parameterTypeDefinitions))
            ?? throw new InvalidOperationException($"Required method '{extensionType.FullName}.{methodName}' was not found.");
    }

    private static bool MatchesGenericMethod(MethodInfo method, string methodName, IReadOnlyList<Type> parameterTypeDefinitions)
    {
        if (!method.IsGenericMethodDefinition || method.Name != methodName)
        {
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length != parameterTypeDefinitions.Count)
        {
            return false;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            var actual = parameters[i].ParameterType;
            var expected = parameterTypeDefinitions[i];
            if (actual.IsGenericType)
            {
                actual = actual.GetGenericTypeDefinition();
            }

            if (actual != expected)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Registration extensions for Monica.Configuration.
/// </summary>
public static class ModuleConfigurationRegistrationExtensions
{
    /// <summary>
    /// Enables unified configuration version control.
    /// </summary>
    /// <returns>The current registration.</returns>
    /// <remarks>
    /// Unified version control is disabled by default. After enabling it, register at least one inclusion
    /// filter through <c>IncludeUnifiedVersionCategories</c>, <c>IncludeUnifiedVersionDefinitions</c>, or
    /// <c>UseUnifiedVersionFilter</c>. The module fails fast if enabled without filters.
    /// </remarks>
    public static ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> UseUnifiedVersionControl(this ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> module)
    {
        module.Configure(options =>
        {
            options.UnifiedVersionControl.Enabled = true;
        });
        return module;
    }

    /// <summary>
    /// Includes configuration definitions with one of the specified categories in unified versions.
    /// </summary>
    /// <param name="module">The Configuration module registration.</param>
    /// <param name="categories">The categories to include.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> IncludeUnifiedVersionCategories(this ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> module, params string[] categories)
    {
        var normalizedCategories = NormalizeFilterValues(categories, nameof(categories));
        module.UseUnifiedVersionControl();
        module.ConfigureServices(context =>
        {
            context.Services.AddSingleton<IConfigurationUnifiedVersionFilter>(
                new ConfigurationUnifiedVersionCategoryFilter(normalizedCategories));
        });
        return module;
    }

    /// <summary>
    /// Includes configuration definitions with one of the specified definition keys in unified versions.
    /// </summary>
    /// <param name="module">The Configuration module registration.</param>
    /// <param name="definitionKeys">The definition keys to include.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> IncludeUnifiedVersionDefinitions(this ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> module, params string[] definitionKeys)
    {
        var normalizedDefinitionKeys = NormalizeFilterValues(definitionKeys, nameof(definitionKeys));
        module.UseUnifiedVersionControl();
        module.ConfigureServices(context =>
        {
            context.Services.AddSingleton<IConfigurationUnifiedVersionFilter>(
                new ConfigurationUnifiedVersionDefinitionKeyFilter(normalizedDefinitionKeys));
        });
        return module;
    }

    /// <summary>
    /// Includes configuration definitions accepted by a predicate in unified versions.
    /// </summary>
    /// <param name="module">The Configuration module registration.</param>
    /// <param name="predicate">The inclusion predicate.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> IncludeUnifiedVersionDefinitions(this ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> module, Func<ConfigurationDefinition, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        module.UseUnifiedVersionControl();
        module.ConfigureServices(context =>
        {
            context.Services.AddSingleton<IConfigurationUnifiedVersionFilter>(
                new ConfigurationUnifiedVersionPredicateFilter(predicate));
        });
        return module;
    }

    /// <summary>
    /// Registers a custom unified version inclusion filter.
    /// </summary>
    /// <typeparam name="TFilter">The filter implementation type.</typeparam>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> UseUnifiedVersionFilter<TFilter>(this ModuleRegistration<ModuleConfiguration, ModuleConfigurationOption> module)
        where TFilter : class, IConfigurationUnifiedVersionFilter
    {
        module.UseUnifiedVersionControl();
        module.ConfigureServices(context =>
        {
            context.Services.AddSingleton<IConfigurationUnifiedVersionFilter, TFilter>();
        });
        return module;
    }

    private static IReadOnlySet<string> NormalizeFilterValues(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);

        var normalized = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one non-empty value is required.", parameterName);
        }

        return normalized.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

}

/// <summary>
/// Module options for Monica.Configuration.
/// </summary>
public sealed class ModuleConfigurationOption : ModuleOptions<ModuleConfiguration>
{
    internal bool InputPlanApplied { get; set; }

    internal ConfigurationSectionPathConvention? InputPlanSectionPathConvention { get; set; }

    /// <summary>
    /// Gets or sets how Monica derives section paths for configuration types that do not set
    /// <see cref="ConfigurationAttribute.SectionPath"/> explicitly.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="ConfigurationSectionPathConvention.ShortTypeName"/>, which binds an options type such as
    /// <c>K8SOptions</c> to the root section <c>K8SOptions</c>. Use
    /// <see cref="ConfigurationSectionPathConvention.ClrFullName"/> when a host intentionally wants namespace-qualified
    /// roots such as <c>Company:Product:K8SOptions</c>.
    /// </remarks>
    public ConfigurationSectionPathConvention DefaultSectionPathConvention { get; set; } =
        ConfigurationSectionPathConvention.ShortTypeName;

    /// <summary>
    /// Gets or sets how Monica handles duplicate resolved section paths across managed configuration definitions.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="ConfigurationDuplicateSectionPathBehavior.FailFast"/> because two definitions bound to the
    /// same section make source inspection, mutation, and bootstrap behavior ambiguous. Use
    /// <see cref="ConfigurationDuplicateSectionPathBehavior.Warning"/> only when a host intentionally accepts the overlap.
    /// </remarks>
    public ConfigurationDuplicateSectionPathBehavior DuplicateSectionPathBehavior { get; set; } =
        ConfigurationDuplicateSectionPathBehavior.FailFast;

    /// <summary>
    /// Gets or sets whether invalid effective Monica-managed values are reported as diagnostics or enforced as
    /// application failures.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="ConfigurationRuntimeValidationBehavior.DiagnosticOnly"/>. Monica still activates the
    /// configured provider, generates the source-aware validation report, logs one warning, and exposes the findings
    /// through its facade and UI, but it does not prevent application startup or managed options resolution. Invalid
    /// values are not made safe by this setting; consumers must tolerate them until an operator corrects the source.
    /// Set this to <see cref="ConfigurationRuntimeValidationBehavior.FailFast"/> when the host must reject startup and
    /// subsequent Microsoft options resolution whenever a managed definition is invalid. Store access, provider
    /// activation, report generation, binding conversion, and definition registration failures remain fatal in both
    /// modes.
    /// </remarks>
    public ConfigurationRuntimeValidationBehavior RuntimeValidationBehavior { get; set; } =
        ConfigurationRuntimeValidationBehavior.DiagnosticOnly;

    /// <summary>
    /// Gets unified configuration version control options.
    /// </summary>
    /// <remarks>
    /// Unified versioning is disabled by default. Use guide methods to enable it and register explicit
    /// inclusion filters for the definitions that should be captured and restorable as unified versions.
    /// </remarks>
    public ConfigurationUnifiedVersionControlOptions UnifiedVersionControl { get; } = new();

    /// <summary>
    /// Gets or sets whether source inventory includes runtime configuration keys that do not belong to
    /// Monica-managed configuration definitions.
    /// </summary>
    /// <remarks>
    /// This is enabled by default so operators can inspect bootstrap, host, and custom provider values from
    /// the configuration storage page. Disable it when a host must hide unmanaged runtime configuration from
    /// Monica.Configuration UI.
    /// </remarks>
    public bool IncludeUnmanagedSourceInventoryItems { get; set; } = true;

    /// <summary>
    /// Gets or sets the stable logical service key used to reconcile configuration metadata published by replicas.
    /// </summary>
    /// <remarks>
    /// Replicas of the same service must use the same value. When omitted, Monica uses the host application name,
    /// which normally matches the entry assembly name. Configure this explicitly when multiple logical services
    /// share an application name or when deployment naming must remain stable across entry-assembly changes.
    /// </remarks>
    public string? PublisherKey { get; set; }

    /// <summary>
    /// Gets or sets the stable identity used to ignore reload notifications produced by this process.
    /// </summary>
    /// <remarks>
    /// The default is generated once when the module option instance is created. Set this explicitly when
    /// host infrastructure already provides a better per-process instance id.
    /// </remarks>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets how long remote Monica projection reload requests are batched before they are applied.
    /// </summary>
    /// <remarks>
    /// The default is 500 milliseconds so multiple mutations in the same burst coalesce into one reload.
    /// </remarks>
    public TimeSpan RemoteReloadDebounceDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets or sets the maximum additional random delay before a remote Monica projection reload is applied.
    /// </summary>
    /// <remarks>
    /// The default is two seconds to reduce thundering-herd pressure when many service instances receive
    /// the same distributed notification.
    /// </remarks>
    public TimeSpan RemoteReloadMaxJitterDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets how long received notification ids are retained for duplicate suppression.
    /// </summary>
    /// <remarks>
    /// The default is five minutes, covering common at-least-once delivery retry windows without retaining
    /// unbounded notification state.
    /// </remarks>
    public TimeSpan RemoteReloadDedupeWindow { get; set; } = TimeSpan.FromMinutes(5);
}
