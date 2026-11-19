using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.AI.Abstractions;
using Monica.AI.AgentCapabilities.Abstractions;
using Monica.AI.Skills.Models;
using Monica.AI.Skills.Services;
using Monica.AI.Services.Support.ModuleCatalog;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.Skills;
using Monica.Core.TypeDiscovery.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Extension methods for configuring Monica's AI skill system.
/// </summary>
public static class ModuleSkillSystemBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Enables class-based AI skill discovery and registration.
        /// </summary>
        /// <param name="action">Optional configuration action.</param>
        /// <returns>The skill-system module registration.</returns>
        public ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> AddAISkillSystem(Action<ModuleSkillSystemOption>? action = null)
        {
            return builder.AddModule<ModuleSkillSystem, ModuleSkillSystemOption>(action);
        }
    }
}

/// <summary>
/// Discovers Monica skill classes and exposes them through Microsoft Agent Skills.
/// </summary>
public sealed class ModuleSkillSystem : MonicaModule<ModuleSkillSystemOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleAI>();
        module.Require<ModuleXmlDocumentation>();
    }

    /// <inheritdoc />
    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleSkillSystemOption> discovery)
    {
        discovery.Match(
            TypeQuery.ConcreteClass.AssignableTo<Skill>(),
            (context, matches) =>
            {
                foreach (var match in matches)
                {
                    var skillType = match.Type;
                    context.Registrations.TryAdd(ServiceDescriptor.Singleton(skillType, skillType));
                    context.Registrations.Add(
                        ServiceDescriptor.Singleton(
                            typeof(Skill),
                            serviceProvider => (Skill)serviceProvider.GetRequiredService(skillType)));
                }
            });
    }

    /// <inheritdoc />
    public override void PostConfigureServices(ModuleContext<ModuleSkillSystemOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton<ILoadedModuleCatalog, ModuleRegistryLoadedModuleCatalog>();
        services.TryAddSingleton<MonicaSkillCatalog>();
        services.TryAddSingleton<MonicaAgentSkillsProviderFactory>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentCapabilitySource, SkillAgentCapabilitySource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAIChatAgentContributor, SkillChatAgentContributor>());
        services.TryAddSingleton(_ => Option.CreateReadOnlyFileAccessOptions());
        services.TryAddSingleton<ReadOnlyFileAccessService>();

        foreach (var registration in Option.ExternalFileSkillRegistrations)
        {
            services.AddSingleton(registration);
        }
    }
}

/// <summary>
/// Configuration options for the AI skill system.
/// </summary>
public sealed class ModuleSkillSystemOption : ModuleOptions<ModuleSkillSystem>
{
    internal List<ExternalFileSkillRegistration> ExternalFileSkillRegistrations { get; } = [];
    internal List<ReadOnlyFileAccessRootRegistration> ReadOnlyFileAccessRoots { get; } = [];

    /// <summary>
    /// Ripgrep executable path used by the read-only file access skill. Defaults to <c>rg</c>, resolved from PATH.
    /// Configure this when the host needs to use a bundled or non-standard ripgrep executable.
    /// </summary>
    public string ReadOnlyFileAccessRipgrepExecutablePath { get; set; } = "rg";

    /// <summary>
    /// Maximum seconds a ripgrep process launched by the read-only file access skill may run before cancellation.
    /// Increase this only for very large configured roots where longer searches are expected.
    /// </summary>
    public int ReadOnlyFileAccessRipgrepTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Maximum number of file-content lines returned by one read-only file access read call.
    /// </summary>
    public int ReadOnlyFileAccessMaxReadLines { get; set; } = 400;

    /// <summary>
    /// Default number of file-content lines returned by one read-only file access read call.
    /// </summary>
    public int ReadOnlyFileAccessDefaultReadLines { get; set; } = 160;

    /// <summary>
    /// Maximum token budget returned by one read-only file access read call after line-window selection.
    /// </summary>
    public int ReadOnlyFileAccessMaxReadTokens { get; set; } = 12000;

    /// <summary>
    /// Default token budget returned by one read-only file access read call after line-window selection.
    /// </summary>
    public int ReadOnlyFileAccessDefaultReadTokens { get; set; } = 6000;

    /// <summary>
    /// Maximum number of rows returned by one read-only file access search or file-listing call.
    /// </summary>
    public int ReadOnlyFileAccessMaxResults { get; set; } = 200;

    /// <summary>
    /// Default number of rows returned by one read-only file access search or file-listing call.
    /// </summary>
    public int ReadOnlyFileAccessDefaultResults { get; set; } = 50;

    /// <summary>
    /// Maximum ripgrep context lines allowed before and after each read-only file access search match.
    /// </summary>
    public int ReadOnlyFileAccessMaxSearchContextLines { get; set; } = 5;

    /// <summary>
    /// Maximum characters returned for one read-only file access search result line. Increase only when callers need
    /// to inspect unusually long generated lines.
    /// </summary>
    public int ReadOnlyFileAccessMaxSearchLineCharacters { get; set; } = 500;

    /// <summary>
    /// Adds one or more file-based Agent Framework skill roots.
    /// </summary>
    /// <param name="skillPaths">
    /// Filesystem roots to scan. Each root may be a single skill directory containing <c>SKILL.md</c> or a package
    /// directory containing multiple skill directories; Agent Framework searches recursively up to two levels deep.
    /// </param>
    /// <param name="scriptRunner">
    /// Optional script runner used when discovered skills contain scripts. Leave <see langword="null"/> only when scripts
    /// should remain discoverable but fail if invoked.
    /// </param>
    /// <param name="options">Optional Agent Framework file-skill discovery options.</param>
    /// <param name="usesSubprocessRunner">Whether the supplied runner is Monica's built-in subprocess runner.</param>
    public void AddFileSkills(
        IEnumerable<string> skillPaths,
        AgentFileSkillScriptRunner? scriptRunner = null,
        AgentFileSkillsSourceOptions? options = null,
        bool usesSubprocessRunner = false)
    {
        ArgumentNullException.ThrowIfNull(skillPaths);

        var normalizedPaths = skillPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            throw new ArgumentException("At least one skill path is required.", nameof(skillPaths));
        }

        ExternalFileSkillRegistrations.Add(
            new ExternalFileSkillRegistration(normalizedPaths, scriptRunner, options, usesSubprocessRunner));
    }

    /// <summary>
    /// Adds a filesystem root that Monica agents may inspect through the built-in read-only file access skill.
    /// </summary>
    /// <param name="name">Stable root identifier used by skill tools. Root names are compared case-insensitively.</param>
    /// <param name="path">Absolute path, or path relative to the running application directory, that agents may inspect.</param>
    /// <param name="description">Optional human-readable purpose shown to agents when roots are listed.</param>
    public void AddReadOnlyFileAccessRoot(string name, string path, string? description = null)
    {
        var registration = new ReadOnlyFileAccessRootRegistration(name, path, description);
        if (ReadOnlyFileAccessRoots.Any(root => string.Equals(root.Name, registration.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Duplicate read-only file access root name '{registration.Name}'.");
        }

        ReadOnlyFileAccessRoots.Add(registration);
    }

    internal ReadOnlyFileAccessOptions CreateReadOnlyFileAccessOptions()
    {
        return new ReadOnlyFileAccessOptions
        {
            Roots = ReadOnlyFileAccessRoots.ToList(),
            RipgrepExecutablePath = ReadOnlyFileAccessRipgrepExecutablePath,
            RipgrepTimeoutSeconds = ReadOnlyFileAccessRipgrepTimeoutSeconds,
            MaxReadLines = ReadOnlyFileAccessMaxReadLines,
            DefaultReadLines = ReadOnlyFileAccessDefaultReadLines,
            MaxReadTokens = ReadOnlyFileAccessMaxReadTokens,
            DefaultReadTokens = ReadOnlyFileAccessDefaultReadTokens,
            MaxResults = ReadOnlyFileAccessMaxResults,
            DefaultResults = ReadOnlyFileAccessDefaultResults,
            MaxSearchContextLines = ReadOnlyFileAccessMaxSearchContextLines,
            MaxSearchLineCharacters = ReadOnlyFileAccessMaxSearchLineCharacters
        };
    }
}

/// <summary>
/// Registration extensions for the AI skill system.
/// </summary>
public static class ModuleSkillSystemRegistrationExtensions
{
    /// <summary>
    /// Registers file-based Agent Framework skills from a single filesystem root.
    /// </summary>
    /// <param name="module">The AI skill-system module registration.</param>
    /// <param name="skillPath">
    /// Root to scan. It may point directly at one skill directory containing <c>SKILL.md</c> or at a package directory
    /// containing multiple skill directories.
    /// </param>
    /// <param name="scriptRunner">Optional runner for file-based scripts.</param>
    /// <param name="options">Optional discovery options for resource directories, script directories, and allowed extensions.</param>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> AddFileSkills(this ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> module,
        string skillPath,
        AgentFileSkillScriptRunner? scriptRunner = null,
        AgentFileSkillsSourceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillPath);
        return module.AddFileSkills([skillPath], scriptRunner, options);
    }

    /// <summary>
    /// Registers file-based Agent Framework skills from one or more filesystem roots.
    /// </summary>
    /// <param name="module">The AI skill-system module registration.</param>
    /// <param name="skillPaths">
    /// Roots to scan. Each path may point directly at one skill directory containing <c>SKILL.md</c> or at a package
    /// directory containing multiple skill directories. Agent Framework searches recursively up to two levels deep.
    /// </param>
    /// <param name="scriptRunner">Optional runner for file-based scripts.</param>
    /// <param name="options">Optional discovery options for resource directories, script directories, and allowed extensions.</param>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> AddFileSkills(this ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> module,
        IEnumerable<string> skillPaths,
        AgentFileSkillScriptRunner? scriptRunner = null,
        AgentFileSkillsSourceOptions? options = null)
    {
        return AddFileSkillsCore(module, skillPaths, scriptRunner, options, usesSubprocessRunner: false);
    }

    /// <summary>
    /// Registers file-based Agent Framework skills and uses Monica's local subprocess runner for skill scripts.
    /// </summary>
    /// <param name="module">The AI skill-system module registration.</param>
    /// <param name="skillPath">
    /// Root to scan. It may point directly at one skill directory containing <c>SKILL.md</c> or at a package directory
    /// containing multiple skill directories.
    /// </param>
    /// <param name="options">Optional discovery options for resource directories, script directories, and allowed extensions.</param>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> AddFileSkillsWithSubprocessRunner(this ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> module,
        string skillPath,
        AgentFileSkillsSourceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillPath);
        return AddFileSkillsCore(
            module,
            [skillPath],
            MonicaSubprocessSkillScriptRunner.RunAsync,
            options,
            usesSubprocessRunner: true);
    }

    /// <summary>
    /// Registers file-based Agent Framework skills and uses Monica's local subprocess runner for skill scripts.
    /// </summary>
    /// <param name="module">The AI skill-system module registration.</param>
    /// <param name="skillPaths">
    /// Roots to scan. Each path may point directly at one skill directory containing <c>SKILL.md</c> or at a package
    /// directory containing multiple skill directories. Agent Framework searches recursively up to two levels deep.
    /// </param>
    /// <param name="options">Optional discovery options for resource directories, script directories, and allowed extensions.</param>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> AddFileSkillsWithSubprocessRunner(this ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> module,
        IEnumerable<string> skillPaths,
        AgentFileSkillsSourceOptions? options = null)
    {
        return AddFileSkillsCore(
            module,
            skillPaths,
            MonicaSubprocessSkillScriptRunner.RunAsync,
            options,
            usesSubprocessRunner: true);
    }

    /// <summary>
    /// Registers a filesystem root that Monica agents may inspect with the built-in read-only file access skill.
    /// </summary>
    /// <param name="module">The AI skill-system module registration.</param>
    /// <param name="name">Stable root identifier used by skill tools. Root names are compared case-insensitively.</param>
    /// <param name="path">Absolute path, or path relative to the running application directory, that agents may inspect.</param>
    /// <param name="description">Optional human-readable purpose shown to agents when roots are listed.</param>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> AddReadOnlyFileAccessRoot(this ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> module,
        string name,
        string path,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        module.Configure(
            option => option.AddReadOnlyFileAccessRoot(name, path, description));
        return module;
    }

    /// <summary>
    /// Configures limits and ripgrep execution settings for the built-in read-only file access skill.
    /// </summary>
    /// <param name="module">The AI skill-system module registration.</param>
    /// <param name="configure">Configuration delegate for read-only file access options.</param>
    /// <returns>The current module registration.</returns>
    public static ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> ConfigureReadOnlyFileAccess(this ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> module, Action<ModuleSkillSystemOption> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        module.Configure(configure);
        return module;
    }

    private static ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> AddFileSkillsCore(
        ModuleRegistration<ModuleSkillSystem, ModuleSkillSystemOption> module,
        IEnumerable<string> skillPaths,
        AgentFileSkillScriptRunner? scriptRunner,
        AgentFileSkillsSourceOptions? options,
        bool usesSubprocessRunner)
    {
        ArgumentNullException.ThrowIfNull(skillPaths);
        var normalizedPaths = skillPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            throw new ArgumentException("At least one skill path is required.", nameof(skillPaths));
        }

        module.Configure(
            option => option.AddFileSkills(normalizedPaths, scriptRunner, options, usesSubprocessRunner));
        return module;
    }

}
