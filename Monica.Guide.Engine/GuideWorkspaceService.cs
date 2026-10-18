using System.Text.Json;
using System.Text.RegularExpressions;

namespace Monica.Guide;

/// <summary>Desired workspace initialization.</summary>
public sealed record GuideWorkspaceInitRequest(
    string Workspace,
    string? Profile,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyList<GuideRuleSwitch>? RuleSwitches = null);

/// <summary>
/// Stable machine outcome of workspace detection. Interactive surfaces localize this
/// code; the English <c>Reason</c> stays the CLI's machine-facing explanation.
/// </summary>
public enum GuideWorkspaceDetectionOutcome
{
    /// <summary>The path is not an existing directory; detection did not run.</summary>
    NotADirectory,

    /// <summary>The canonical Monica framework repository (remote identity or characteristics).</summary>
    FrameworkRepository,

    /// <summary>Unambiguous Monica extension characteristics.</summary>
    ExtensionCharacteristics,

    /// <summary>Unambiguous Monica package or project references.</summary>
    ApplicationCharacteristics,

    /// <summary>Both application and extension characteristics; a profile must be chosen explicitly.</summary>
    AmbiguousApplicationAndExtension,

    /// <summary>Application characteristics, but bounded source scanning could not rule out an extension.</summary>
    AmbiguousApplicationUnscanned,

    /// <summary>The bounded source scan was exhausted before any decisive characteristic.</summary>
    AmbiguousScanExhausted,

    /// <summary>No canonical identity or characteristic files at all.</summary>
    AmbiguousNoCharacteristics
}

/// <summary>
/// Workspace bootstrap for skill-catalog products. <c>init</c> writes the repository-shared
/// <c>.monica/guide.json</c> profile configuration and one guide-owned instruction block into
/// the root <c>AGENTS.md</c> (plus the minimal <c>@AGENTS.md</c> import for Claude Code when a
/// Claude target is installed). Every mutation is preview-first and digest-locked like every
/// other guide operation; skill installation stays with <c>configure</c>.
/// </summary>
public sealed partial class GuideWorkspaceService
{
    private const string PROFILE_APPLICATION = "application";
    private const string PROFILE_EXTENSION = "extension-author";
    private const string PROFILE_FRAMEWORK = "framework-contributor";
    private const string CAPABILITY_MICROSERVICE = "microservice";
    private const string CAPABILITY_MODULAR_MONOLITH = "modular-monolith";
    private const string CAPABILITY_UI = "ui";
    private const string AGENTS_FILE_NAME = "AGENTS.md";
    private const string CLAUDE_FILE_NAME = "CLAUDE.md";
    private const string CLAUDE_IMPORT_LINE = "@AGENTS.md";
    private const string CONFIG_DIRECTORY_NAME = ".monica";
    private const string CONFIG_FILE_NAME = "guide.json";

    /// <summary>Bounded source-scan limits, ported from the retired Node engine unchanged.</summary>
    private const int SOURCE_SCAN_FILE_LIMIT = 250;
    private const int SOURCE_SCAN_BYTE_LIMIT = 16 * 1024 * 1024;

    private static readonly string[] IGNORED_DIRECTORIES =
    [
        ".git", ".tmp", "bin", "obj", "artifacts", "node_modules", "skills", ".agents", ".claude", ".codex"
    ];

    private readonly AgentProductDefinition _definition;
    private readonly GuidePaths _enginePaths;
    private readonly SkillCatalog? _catalog;
    private readonly AgentProductPaths _productPaths;
    private readonly IGuideGitProbe _git;

    /// <summary>
    /// The catalog may be null for registry-only surfaces (for example listing workspaces
    /// before the product is installed); init and inspect require one.
    /// </summary>
    public GuideWorkspaceService(
        AgentProductDefinition definition,
        GuidePaths enginePaths,
        SkillCatalog? catalog,
        AgentProductPaths? productPaths = null,
        IGuideGitProbe? git = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(enginePaths);
        _definition = definition;
        _enginePaths = enginePaths;
        _catalog = catalog;
        _productPaths = productPaths ?? AgentProductPaths.ForCurrentUser(definition);
        _git = git ?? new GuideGitProbe();
    }

    /// <summary>
    /// Loads the installed product's packaged skill catalog, or null when no usable catalog
    /// exists. The ledger's recorded bundle wins; before the first configure the bundle that
    /// ships this running executable is used, so a fresh guide can initialize workspaces
    /// straight from its own release.
    /// </summary>
    public static SkillCatalog? TryLoadProductCatalog(AgentProductDefinition definition, GuidePaths enginePaths)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(enginePaths);
        var bundleRoots = new List<string>();
        try
        {
            if (File.Exists(enginePaths.GuideLedgerFile))
            {
                var ledger = JsonSerializer.Deserialize<GuideLedger>(
                    File.ReadAllBytes(enginePaths.GuideLedgerFile), GuidePlanning.JsonOptions);
                var state = ledger?.Products.FirstOrDefault(product =>
                    string.Equals(product.ProductId, definition.ProductId, StringComparison.Ordinal));
                if (state is not null)
                {
                    bundleRoots.Add(state.BundleRoot);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // A corrupt ledger is reported by the configure/status flows; here it just means
            // no recorded bundle is available.
        }

        if (RunningBundleRoot() is { } running)
        {
            bundleRoots.Add(running);
        }

        foreach (var bundleRoot in bundleRoots)
        {
            try
            {
                var catalog = GuideReleaseMetadata.LoadSkillCatalog(Path.Combine(bundleRoot, "skills"));
                if (catalog.ManagedInstructions is not null)
                {
                    return catalog;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                // Try the next candidate bundle; a legacy or corrupt catalog (for example one
                // predating the current managed-instruction contract) is not machine-fatal.
            }
        }

        return null;
    }

    /// <summary>The bundle surrounding this running executable, when there is one.</summary>
    private static string? RunningBundleRoot()
    {
        try
        {
            var manifestPath = GuideReleaseMetadata.ResolveManifestPath(AppContext.BaseDirectory);
            return File.Exists(manifestPath)
                ? Path.GetDirectoryName(manifestPath)
                : null;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Advisory workspace detection for interactive surfaces (the wizard's add flow).</summary>
    public sealed record GuideWorkspaceCandidate(
        GuideWorkspaceDetectionOutcome Outcome,
        string? CandidateProfile,
        string Confidence,
        string Reason,
        IReadOnlyList<string> Capabilities,
        string? FrameworkVersion,
        string? FrameworkVersionTier,
        bool Initialized,
        string? ConfiguredProfile,
        IReadOnlyList<string>? ConfiguredCapabilities = null,
        string? InitializingProduct = null);

    /// <summary>Detects one workspace candidate without mutating anything.</summary>
    public GuideWorkspaceCandidate DetectCandidate(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var root = Path.GetFullPath(workspace);
        if (!Directory.Exists(root))
        {
            return new GuideWorkspaceCandidate(
                GuideWorkspaceDetectionOutcome.NotADirectory,
                null, string.Empty, $"Workspace is not a directory: {root}.",
                [], null, null, false, null);
        }

        var detection = Detect(root);
        var config = GuideWorkspaceStore.LoadConfig(root, out _);
        return new GuideWorkspaceCandidate(
            detection.Outcome,
            detection.CandidateProfile,
            detection.Confidence,
            detection.Reason,
            detection.Capabilities,
            detection.FrameworkVersion,
            detection.FrameworkVersionTier,
            config is not null,
            config?.Profile,
            config?.Capabilities,
            config?.ProductId);
    }

    /// <summary>
    /// Names of the skill trees a workspace configure installs for one profile, honoring the
    /// global-first guide-skill exclusion, so interactive surfaces can preview the closure
    /// before any plan exists.
    /// </summary>
    public IReadOnlyList<string> ProfileClosureSkills(string profile, IReadOnlyList<string>? capabilities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        if (_catalog is null)
        {
            throw new InvalidOperationException("No installed release catalog is available.");
        }

        var closure = AgentGuideService.SelectProfileClosure(
            _catalog,
            profile,
            [],
            AgentGuideService.WorkspaceGuideSkillExclusion(_definition, _productPaths),
            capabilities);
        return closure is null
            ? []
            : closure.Select(static skill => skill.Name).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Read-only workspace health for status and doctor surfaces.</summary>
    public GuideReport Inspect(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var checks = new List<GuideCheck>();
        var root = Path.GetFullPath(workspace);
        if (!Directory.Exists(root))
        {
            checks.Add(Check("workspace.root", GuideCheckStatus.Error, $"Workspace is not a directory: {root}."));
            return Report(checks, "Workspace inspection failed.", []);
        }

        var detection = Detect(root);
        var config = LoadConfig(root, checks);
        InspectProfile(root, detection, config, checks);
        InspectInstructions(root, config, checks);
        InspectSkills(root, config, checks);
        return Report(
            checks,
            $"Workspace {root}: {detection.CandidateProfile ?? "no candidate profile"} detected ({detection.Confidence}).",
            ["Install the profile skills with: guide configure --workspace <path>."]);
    }

    /// <summary>
    /// Lists every registered workspace with a live observation: directory presence,
    /// configuration currency, installed-versus-profile skill counts, and instruction health.
    /// </summary>
    public IReadOnlyList<GuideWorkspaceView> ListWorkspaces()
    {
        var registry = GuideWorkspaceRegistryFile.Load(_enginePaths);
        if (registry is null)
        {
            return [];
        }

        var views = new List<GuideWorkspaceView>();
        // Each product's surfaces list only their own registrations: the registry is
        // engine-global, but a workspace guided by several products is judged by each
        // product against its own catalog, never against a sibling product's version.
        var instructionsVersion = _catalog?.ManagedInstructions?.Version;
        foreach (var entry in registry.Workspaces.Where(entry =>
                     string.Equals(entry.ProductId, _definition.ProductId, StringComparison.Ordinal)))
        {
            var issues = new List<string>();
            var exists = Directory.Exists(entry.Workspace);
            var config = exists ? GuideWorkspaceStore.LoadConfig(entry.Workspace, out _) : null;
            var configurationCurrent = config is not null
                                       && string.Equals(config.Profile, entry.Profile, StringComparison.Ordinal)
                                       && (instructionsVersion is null
                                           || entry.InstructionBlockVersion == instructionsVersion);
            var (installed, profileCount) = CountWorkspaceSkills(
                entry.Workspace, config?.Profile ?? entry.Profile, config?.Capabilities ?? entry.Capabilities);
            var instructionsCurrent = false;
            var instructionsComparable = false;
            if (config is not null)
            {
                var managed = _catalog?.ManagedInstructions;
                var agentsText = ReadText(Path.Combine(entry.Workspace, AGENTS_FILE_NAME));
                // Without a catalog the expected body is unknowable, so staleness stays silent.
                instructionsComparable = managed is not null && managed.Templates.ContainsKey(config.Profile);
                instructionsCurrent = instructionsComparable
                                      && GuideInstructionBlock.State(agentsText, managed!.Markers) == GuideInstructionBlockStatus.Valid
                                      && InstructionsMatch(agentsText, config.Profile, managed.Markers, config.DisabledRules);
            }

            if (!exists)
            {
                issues.Add("The workspace directory no longer exists.");
            }
            else if (config is null)
            {
                issues.Add("The workspace configuration is missing or unreadable.");
            }
            else if (!configurationCurrent)
            {
                issues.Add("The workspace was re-initialized with a different profile or instruction version.");
            }
            else if (instructionsComparable && !instructionsCurrent)
            {
                issues.Add("The managed instruction block is stale; update the workspace to converge it.");
            }
            if (exists && _catalog?.ManagedInstructions is { } instructions
                && GuideInstructionBlock.State(ReadText(Path.Combine(entry.Workspace, CLAUDE_FILE_NAME)), instructions.Markers)
                   != GuideInstructionBlockStatus.Absent)
                issues.Add($"Root {CLAUDE_FILE_NAME} carries a legacy product instruction span; update the workspace to converge it.");

            if (exists && profileCount > 0 && installed < profileCount)
            {
                issues.Add($"Only {installed} of {profileCount} profile skills are installed.");
            }

            views.Add(new GuideWorkspaceView(
                entry.Workspace,
                entry.ProductId,
                config?.Profile ?? entry.Profile,
                config?.Capabilities ?? entry.Capabilities,
                exists,
                configurationCurrent,
                installed,
                profileCount,
                instructionsCurrent,
                issues));
        }

        return views
            .OrderBy(static view => view.Workspace, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Renders the exact managed instruction span the next initialization or update writes
    /// for one profile, from the catalog template plus the current machine projection state.
    /// A null or unknown profile resolves to the catalog's first template so preview surfaces
    /// can show an example before any workspace is registered.
    /// </summary>
    public GuideInstructionPreview PreviewManagedInstructions(string? profile = null)
    {
        var instructions = Instructions;
        var resolved = profile is not null && instructions.Templates.ContainsKey(profile)
            ? profile
            : instructions.Templates.Keys.Order(StringComparer.Ordinal).First();
        return new GuideInstructionPreview(
            resolved,
            instructions.Markers.Start,
            instructions.Markers.End,
            RenderInstructions(resolved));
    }

    /// <summary>
    /// Describes the managed instruction rules of one initialized workspace's profile with
    /// their current switch states. Setup surfaces render these as per-workspace toggles;
    /// the switches themselves apply through the digest-locked init/configure flow. Throws
    /// <see cref="InvalidOperationException"/> when the workspace is uninitialized, the
    /// catalog has no instructions, or the configured profile has no template.
    /// </summary>
    public GuideWorkspaceRuleStates DescribeManagedRules(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var config = GuideWorkspaceStore.LoadConfig(Path.GetFullPath(workspace), out _);
        var profile = config?.Profile;
        if (config is null || profile is null || !Instructions.Templates.ContainsKey(profile))
        {
            throw new InvalidOperationException(
                $"The workspace defines no managed instruction rules for profile '{profile}'.");
        }

        var disabled = config.EffectiveDisabledRules() is { Count: > 0 } disabledIds
            ? new HashSet<string>(disabledIds, StringComparer.Ordinal)
            : [];
        return new GuideWorkspaceRuleStates(
            profile,
            Instructions.Templates[profile].Rules
                .Select(rule => new GuideManagedRuleState(rule.Id, rule.Text, !disabled.Contains(rule.Id)))
                .ToArray());
    }

    /// <summary>Compares this workspace's recorded installations against its profile closure.</summary>
    private (int Installed, int ProfileCount) CountWorkspaceSkills(
        string workspace, string profile, IReadOnlyList<string> capabilities)
    {
        var closure = _catalog is not null
            ? AgentGuideService.SelectProfileClosure(
                _catalog,
                profile,
                new List<GuideCheck>(),
                AgentGuideService.WorkspaceGuideSkillExclusion(_definition, _productPaths),
                capabilities)
            : null;
        if (closure is null || !File.Exists(_enginePaths.GuideLedgerFile))
        {
            return (0, closure?.Count ?? 0);
        }

        try
        {
            var ledger = JsonSerializer.Deserialize<GuideLedger>(
                File.ReadAllBytes(_enginePaths.GuideLedgerFile), GuidePlanning.JsonOptions);
            var installed = (ledger?.Products ?? [])
                .SelectMany(static product => product.Installations)
                .Where(installation => installation.WorkspaceRoot is not null
                                       && string.Equals(
                                           AgentGuideService.NormalizeRoot(installation.WorkspaceRoot),
                                           AgentGuideService.NormalizeRoot(workspace),
                                           StringComparison.OrdinalIgnoreCase))
                .SelectMany(static installation => installation.Trees)
                .Select(static tree => tree.Name)
                .Distinct(StringComparer.Ordinal)
                .Count(name => closure!.Any(skill => skill.Name == name));
            return (installed, closure.Count);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return (0, closure.Count);
        }
    }

    private void InspectSkills(string root, GuideWorkspaceStore.GuideWorkspaceConfig? config, List<GuideCheck> checks)
    {
        if (config is null)
        {
            return;
        }

        var (installed, profileCount) = CountWorkspaceSkills(root, config.Profile, config.Capabilities);
        if (profileCount == 0)
        {
            checks.Add(Check("workspace.skills", GuideCheckStatus.Warning,
                $"The installed catalog declares no skills for profile '{config.Profile}'."));
            return;
        }

        checks.Add(installed == profileCount
            ? Check("workspace.skills", GuideCheckStatus.Ok,
                $"All {installed} profile skills are installed in the configured project directories.",
                null,
                new Dictionary<string, string> { ["installed"] = installed.ToString(), ["profile"] = profileCount.ToString() })
            : Check("workspace.skills", GuideCheckStatus.Warning,
                installed == 0
                    ? $"No profile skills are installed in the workspace ({profileCount} expected)."
                    : $"Only {installed} of {profileCount} profile skills are installed in the workspace.",
                "Run guide configure --workspace <path> and approve the plan.",
                new Dictionary<string, string> { ["installed"] = installed.ToString(), ["profile"] = profileCount.ToString() }));
    }

    /// <summary>Previews or applies workspace initialization.</summary>
    public async Task<GuideReport> InitAsync(
        GuideWorkspaceInitRequest request,
        string? expectedDigest = null,
        IProgress<GuidePhase>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var guideLock = expectedDigest is not null
            ? await GuideMutations.AcquireLockAsync(_enginePaths, cancellationToken)
            : null;
        var (prepared, blockers) = BuildInitPlan(request);
        return await CompleteAsync(prepared, blockers, expectedDigest, progress, cancellationToken);
    }

    /// <summary>
    /// Converges the managed instruction surface during a workspace configure, so machine
    /// projection switches (source hints, issue policy), per-workspace rule switches, and
    /// catalog template changes apply with the same update that refreshes skills. The
    /// repository-shared configuration and the registry entry record the block version
    /// actually written.
    /// </summary>
    internal void ConvergeInstructions(
        string workspaceRoot,
        ICollection<GuideCheck> checks,
        List<GuidePlannedMutation> mutations,
        IReadOnlyList<GuideRuleSwitch>? ruleSwitches = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var root = Path.GetFullPath(workspaceRoot);
        var config = GuideWorkspaceStore.LoadConfig(root, out _);
        if (config is null || !Instructions.Templates.ContainsKey(config.Profile))
        {
            return;
        }

        config = ApplyRuleSwitches(config.DisabledRules, ruleSwitches, checks, out var switchesChanged) is { } disabled
            ? config with { DisabledRules = disabled }
            : config with { DisabledRules = null };

        var markers = Instructions.Markers;
        var agentsPath = Path.Combine(root, AGENTS_FILE_NAME);
        var agentsText = ReadText(agentsPath);
        if (GuideInstructionBlock.State(agentsText, markers) == GuideInstructionBlockStatus.Malformed)
        {
            checks.Add(Check("workspace.instructions", GuideCheckStatus.Error,
                $"Root {AGENTS_FILE_NAME} contains malformed or duplicate guide markers."));
            return;
        }
        var claudePath = Path.Combine(root, CLAUDE_FILE_NAME);
        var claudeText = ReadText(claudePath);
        if (!ValidateClaudeInstructions(claudeText, checks)) return;

        var changed = false;
        var agentsAfter = GuideInstructionBlock.Upsert(
            agentsText,
            RenderInstructions(config.Profile, config.DisabledRules),
            markers);
        if (!string.Equals(agentsText, agentsAfter, StringComparison.Ordinal))
        {
            GuideMutations.AddLocalWrite(
                "workspace.instructions.agents",
                agentsPath,
                GuidePlanning.Utf8(agentsAfter),
                $"Update the root managed instruction block in {AGENTS_FILE_NAME} to the current catalog and machine state.",
                mutations);
            changed = true;
        }

        var wantsClaude = WantsClaudeImport(config);
        changed = ConvergeClaudeInstructions(
            claudePath, claudeText, wantsClaude, config.ManagedClaudeImport, mutations) || changed;

        if (changed)
        {
            checks.Add(Check("workspace.instructions.converged", GuideCheckStatus.Ok,
                $"The managed instruction block in {AGENTS_FILE_NAME} converges with the current catalog and machine state."));
        }

        // The registry entry carries this product's instruction version, and the config write
        // lands only when a workspace-shared fact actually changed: rule switches, the Claude
        // import preference, or the recorded block version.
        var registry = GuideWorkspaceRegistryFile.Load(_enginePaths);
        if (switchesChanged || config.ManagedClaudeImport != wantsClaude)
        {
            GuideMutations.AddLocalWrite(
                "workspace.config.write",
                GuideWorkspaceStore.ConfigPath(root),
                GuidePlanning.JsonBytes(config with { ManagedClaudeImport = wantsClaude }),
                switchesChanged
                    ? "Record the converged managed-rule switches and Claude-import preference in the workspace configuration."
                    : "Record the converged Claude-import preference in the workspace configuration.",
                mutations);
        }

        if (RegisteredEntry(registry, root)?.InstructionBlockVersion == Instructions.Version)
        {
            return;
        }
        GuideMutations.AddLocalWrite(
            "workspace.registry.write",
            GuideWorkspaceRegistryFile.PathFor(_enginePaths),
            GuidePlanning.JsonBytes(GuideWorkspaceRegistryFile.Upsert(
                registry,
                new GuideWorkspaceEntry(
                    root,
                    _definition.ProductId,
                    config.Profile,
                    config.Capabilities,
                    Instructions.Version))),
            "Record the converged instruction block version in the engine registry.",
            mutations);
    }

    /// <summary>
    /// Applies requested rule switches onto one disabled-rule list. Rules default on, so
    /// <c>off</c> adds the id and <c>on</c> removes it; an empty result returns null so the
    /// repository-shared configuration stays clean. Unknown ids are reported as error checks
    /// and skipped, so a typo can never silently reshape the managed block; init passes its
    /// blocker list so the same failure aborts the plan before any write.
    /// </summary>
    private IReadOnlyList<string>? ApplyRuleSwitches(
        IReadOnlyList<string>? currentDisabled,
        IReadOnlyList<GuideRuleSwitch>? ruleSwitches,
        ICollection<GuideCheck> checks,
        out bool changed)
    {
        changed = false;
        if (ruleSwitches is null || ruleSwitches.Count == 0)
        {
            return currentDisabled;
        }

        var known = KnownRuleIds();
        var disabled = currentDisabled is { Count: > 0 } existing
            ? new HashSet<string>(existing, StringComparer.Ordinal)
            : [];
        foreach (var ruleSwitch in ruleSwitches)
        {
            var id = ruleSwitch.RuleId.Trim();
            if (!known.Contains(id))
            {
                checks.Add(Check("workspace.rule-switch", GuideCheckStatus.Error,
                    $"Unknown managed rule '{id}'.",
                    $"Known rule ids: {string.Join(", ", known.Order(StringComparer.Ordinal))}."));
                continue;
            }

            changed |= ruleSwitch.Enabled ? disabled.Remove(id) : disabled.Add(id);
        }

        if (!changed)
        {
            return currentDisabled;
        }

        var ordered = disabled.Order(StringComparer.Ordinal).ToArray();
        return ordered.Length > 0 ? ordered : null;
    }

    /// <summary>Every rule id the current catalog can render, across all profile templates.</summary>
    private IReadOnlySet<string> KnownRuleIds()
        => Instructions.Templates.Values
            .SelectMany(static template => template.Rules)
            .Select(static rule => rule.Id)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>This product's registry entry for one workspace, if registered.</summary>
    private GuideWorkspaceEntry? RegisteredEntry(GuideWorkspaceRegistry? registry, string root)
        => (registry?.Workspaces ?? []).FirstOrDefault(entry =>
            string.Equals(entry.Workspace, root, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.ProductId, _definition.ProductId, StringComparison.Ordinal));

    /// <summary>Previews or applies removing every guide-owned workspace artifact.</summary>
    public async Task<GuideReport> ForgetAsync(
        string workspace,
        string? expectedDigest = null,
        IProgress<GuidePhase>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        await using var guideLock = expectedDigest is not null
            ? await GuideMutations.AcquireLockAsync(_enginePaths, cancellationToken)
            : null;
        var (prepared, blockers) = BuildForgetPlan(workspace);
        return await CompleteAsync(prepared, blockers, expectedDigest, progress, cancellationToken);
    }

    private (GuidePreparedPlan Plan, IReadOnlyList<GuideCheck> Blockers) BuildInitPlan(GuideWorkspaceInitRequest request)
    {
        var blockers = new List<GuideCheck>();
        var checks = new List<GuideCheck>();
        var mutations = new List<GuidePlannedMutation>();
        var root = Path.GetFullPath(request.Workspace);
        if (!Directory.Exists(root))
        {
            blockers.Add(Check("workspace.root", GuideCheckStatus.Error, $"Workspace is not a directory: {root}."));
            return (GuideMutations.Prepare("init", mutations, blockers), blockers);
        }

        var detection = Detect(root);
        var config = LoadConfig(root, checks);
        var profile = request.Profile?.Trim();
        if (string.IsNullOrWhiteSpace(profile))
        {
            blockers.Add(Check("workspace.profile", GuideCheckStatus.Error,
                detection.CandidateProfile is null
                    ? "Select a profile explicitly; detection found no unambiguous candidate."
                    : $"Select a profile explicitly; detected candidate: {detection.CandidateProfile} ({detection.Reason}).",
                $"Known profiles: {ProfileList()}."));
        }
        else if (!Instructions.Templates.ContainsKey(profile))
        {
            blockers.Add(Check("workspace.profile", GuideCheckStatus.Error,
                $"Unknown profile '{profile}'.", $"Known profiles: {ProfileList()}."));
        }

        var capabilities = (request.Capabilities ?? [])
            .Select(static item => item.Trim().ToLowerInvariant())
            .Where(static item => item.Length > 0)
            .Distinct()
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();
        var unknownCapabilities = capabilities.Where(item => !IsKnownCapability(item)).ToArray();
        if (unknownCapabilities.Length > 0)
        {
            blockers.Add(Check("workspace.capabilities", GuideCheckStatus.Error,
                $"Unknown capabilities: {string.Join(", ", unknownCapabilities)}.",
                $"Known capabilities: {CAPABILITY_MICROSERVICE}, {CAPABILITY_MODULAR_MONOLITH}, {CAPABILITY_UI}."));
        }

        blockers.AddRange(ProfileRepositoryIssues(root, detection, profile));
        if (profile == PROFILE_EXTENSION && detection.HasMonicaProjectReference)
        {
            blockers.Add(Check("workspace.project-reference", GuideCheckStatus.Error,
                "Extension projects must consume Monica through immutable NuGet packages; a Monica ProjectReference was detected.",
                "Bind exact Monica source separately as a lookup locator; the binding grants no write permission."));
        }

        var markers = Instructions.Markers;
        var agentsPath = Path.Combine(root, AGENTS_FILE_NAME);
        var agentsText = ReadText(agentsPath);
        if (GuideInstructionBlock.State(agentsText, markers) == GuideInstructionBlockStatus.Malformed)
        {
            blockers.Add(Check("workspace.instructions", GuideCheckStatus.Error,
                $"Root {AGENTS_FILE_NAME} contains malformed or duplicate guide markers."));
        }

        var claudePath = Path.Combine(root, CLAUDE_FILE_NAME);
        var claudeText = ReadText(claudePath);
        ValidateClaudeInstructions(claudeText, blockers);

        // Rule switches validate before the write plan, so an unknown id blocks initialization
        // exactly like an unknown profile or capability; re-init preserves previously
        // disabled rules that the request does not switch back on.
        var disabledRules = ApplyRuleSwitches(config?.DisabledRules, request.RuleSwitches, blockers, out _);

        if (blockers.Count == 0)
        {
            var instructionsChanged = false;
            var wantsClaude = WantsClaudeImport(config);
            var nextConfig = new GuideWorkspaceStore.GuideWorkspaceConfig(
                GuideWorkspaceStore.GuideWorkspaceConfig.CurrentSchemaVersion,
                _definition.ProductId,
                profile!,
                capabilities,
                wantsClaude,
                config?.SkillTargets,
                disabledRules);

            GuideMutations.AddLocalWrite(
                "workspace.config.write",
                GuideWorkspaceStore.ConfigPath(root),
                GuidePlanning.JsonBytes(nextConfig),
                "Write repository-shared guide workspace configuration.",
                mutations);
            GuideMutations.AddLocalWrite(
                "workspace.registry.write",
                GuideWorkspaceRegistryFile.PathFor(_enginePaths),
                GuidePlanning.JsonBytes(GuideWorkspaceRegistryFile.Upsert(
                    GuideWorkspaceRegistryFile.Load(_enginePaths),
                    new GuideWorkspaceEntry(
                        root,
                        _definition.ProductId,
                        profile!,
                        capabilities,
                        Instructions.Version))),
                "Record the initialized workspace in the engine registry.",
                mutations);

            var managedBody = RenderInstructions(profile!, disabledRules);
            var agentsAfter = GuideInstructionBlock.Upsert(agentsText, managedBody, markers);
            if (!string.Equals(agentsText, agentsAfter, StringComparison.Ordinal))
            {
                GuideMutations.AddLocalWrite(
                    "workspace.instructions.agents",
                    agentsPath,
                    GuidePlanning.Utf8(agentsAfter),
                    $"Create or update only the root managed instruction block in {AGENTS_FILE_NAME}.",
                    mutations);
                instructionsChanged = true;
            }

            instructionsChanged = ConvergeClaudeInstructions(
                claudePath, claudeText, wantsClaude, config?.ManagedClaudeImport == true, mutations) || instructionsChanged;

            if (instructionsChanged)
            {
                checks.Add(Check("workspace.instructions.reload", GuideCheckStatus.Warning,
                    $"{AGENTS_FILE_NAME} or {CLAUDE_FILE_NAME} will change; start a new agent session after applying."));
            }

            foreach (var nested in detection.NestedInstructionFiles)
            {
                checks.Add(Check("workspace.instructions.nested", GuideCheckStatus.Warning,
                    $"Nested instruction file was diagnosed but is not managed: {nested}."));
            }

            if (detection.FrameworkVersion is not null)
            {
                checks.Add(Check("workspace.framework", GuideCheckStatus.Ok,
                    $"Resolved {_definition.DisplayName} {detection.FrameworkVersion} from {detection.FrameworkVersionTier}."));
            }
            else if (detection.FrameworkVersionIssues.Count > 0)
            {
                foreach (var issue in detection.FrameworkVersionIssues)
                {
                    checks.Add(Check("workspace.framework", GuideCheckStatus.Warning, issue));
                }
            }
        }

        var prepared = GuideMutations.Prepare("init", mutations, [.. checks, .. blockers]);
        return (prepared, blockers);
    }

    private (GuidePreparedPlan Plan, IReadOnlyList<GuideCheck> Blockers) BuildForgetPlan(string workspace)
    {
        var blockers = new List<GuideCheck>();
        var checks = new List<GuideCheck>();
        var mutations = new List<GuidePlannedMutation>();
        var root = Path.GetFullPath(workspace);
        if (!Directory.Exists(root))
        {
            blockers.Add(Check("workspace.root", GuideCheckStatus.Error, $"Workspace is not a directory: {root}."));
            return (GuideMutations.Prepare("forget", mutations, blockers), blockers);
        }

        var config = LoadConfig(root, checks);
        var markers = Instructions.Markers;
        var agentsPath = Path.Combine(root, AGENTS_FILE_NAME);
        var agentsText = ReadText(agentsPath);
        if (GuideInstructionBlock.State(agentsText, markers) == GuideInstructionBlockStatus.Malformed)
        {
            blockers.Add(Check("workspace.instructions", GuideCheckStatus.Error,
                $"Root {AGENTS_FILE_NAME} contains malformed or duplicate guide markers; remove the block manually."));
        }

        if (config is null)
        {
            checks.Add(Check("workspace.config", GuideCheckStatus.Warning,
                "This repository has no guide workspace configuration."));
        }

        var agentsAfter = GuideInstructionBlock.Remove(agentsText, markers);
        if (!string.Equals(agentsText, agentsAfter, StringComparison.Ordinal))
        {
            GuideMutations.AddLocalWrite(
                "workspace.instructions.agents",
                agentsPath,
                GuidePlanning.Utf8(agentsAfter),
                $"Remove only the root managed instruction block from {AGENTS_FILE_NAME}.",
                mutations);
        }

        var claudePath = Path.Combine(root, CLAUDE_FILE_NAME);
        var claudeText = ReadText(claudePath);
        if (config?.ManagedClaudeImport == true && claudeText.Length > 0)
        {
            var claudeAfter = GuideInstructionBlock.RemoveClaudeImport(claudeText);
            if (!string.Equals(claudeText, claudeAfter, StringComparison.Ordinal))
            {
                AddClaudeImportRemoval(claudePath, claudeAfter, mutations);
            }
        }

        GuideMutations.AddLocalDelete(
            "workspace.config.remove",
            GuideWorkspaceStore.ConfigPath(root),
            "Remove repository-shared guide workspace configuration.",
            mutations);
        if (config is not null)
        {
            mutations.Add(new GuideDirectoryCleanup(
                new GuidePlanAction(
                    "workspace.config.remove.directory",
                    GuidePlanActionKind.DeleteFile,
                    Path.GetDirectoryName(GuideWorkspaceStore.ConfigPath(root))!,
                    null,
                    null,
                    "Remove the emptied .monica configuration directory."),
                Path.GetDirectoryName(GuideWorkspaceStore.ConfigPath(root))!));
        }
        GuideMutations.AddLocalWrite(
            "workspace.registry.write",
            GuideWorkspaceRegistryFile.PathFor(_enginePaths),
            GuidePlanning.JsonBytes(GuideWorkspaceRegistryFile.Remove(
                GuideWorkspaceRegistryFile.Load(_enginePaths),
                root,
                _definition.ProductId)),
            "Remove the workspace from the engine registry.",
            mutations);
        ForgetProjectInstallations(root, checks, mutations);

        var prepared = GuideMutations.Prepare("forget", mutations, [.. checks, .. blockers]);
        return (prepared, blockers);
    }

    /// <summary>
    /// Removes every project-local skill installation this product recorded inside the
    /// workspace: the skill trees go first, then the ledger entry keeps its remaining
    /// installations. Global targets are untouched.
    /// </summary>
    private void ForgetProjectInstallations(
        string root,
        ICollection<GuideCheck> checks,
        ICollection<GuidePlannedMutation> mutations)
    {
        GuideLedger? ledger;
        try
        {
            if (!File.Exists(_enginePaths.GuideLedgerFile))
            {
                return;
            }

            ledger = JsonSerializer.Deserialize<GuideLedger>(
                File.ReadAllBytes(_enginePaths.GuideLedgerFile), GuidePlanning.JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            checks.Add(Check("workspace.skills", GuideCheckStatus.Warning,
                $"The guide ownership ledger is unreadable; project skills were left in place: {exception.Message}"));
            return;
        }

        var state = ledger?.Products.FirstOrDefault(product =>
            string.Equals(product.ProductId, _definition.ProductId, StringComparison.Ordinal));
        if (state is null)
        {
            return;
        }

        var workspaceInstallations = state.Installations
            .Where(installation => installation.WorkspaceRoot is not null
                                   && string.Equals(
                                       AgentGuideService.NormalizeRoot(installation.WorkspaceRoot),
                                       AgentGuideService.NormalizeRoot(root),
                                       StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var installation in workspaceInstallations)
        {
            var targetName = AgentGuideService.TargetCheckName(installation.Target, installation.TargetRoot);
            var action = new GuidePlanAction(
                $"workspace.skills.remove.{targetName}",
                GuidePlanActionKind.DeleteSkillDirectory,
                installation.TargetRoot,
                null,
                null,
                $"Delete the {installation.Trees.Count} guide-installed skill directories from {installation.TargetRoot}.");
            mutations.Add(new GuideProjectTreeRemove(
                action,
                installation.TargetRoot,
                installation.Trees.Select(static tree => tree.Name).ToArray()));
        }

        if (workspaceInstallations.Length == 0)
        {
            return;
        }

        var retained = state.Installations.Except(workspaceInstallations).ToArray();
        var products = ledger!.Products
            .Where(product => !string.Equals(product.ProductId, state.ProductId, StringComparison.Ordinal))
            .ToList();
        products.Add(state with { Installations = retained });
        GuideMutations.AddLocalWrite(
            "configuration.state.write",
            _enginePaths.GuideLedgerFile,
            GuidePlanning.JsonBytes(new GuideLedger(GuideLedger.CurrentSchemaVersion,
                products.OrderBy(static product => product.ProductId, StringComparer.Ordinal).ToArray())),
            "Remove the workspace's project skill installations from the unified ledger.",
            mutations);
    }

    private static bool AddClaudeImportRemoval(
        string claudePath,
        string claudeAfter,
        List<GuidePlannedMutation> mutations)
    {
        if (claudeAfter.Length == 0)
        {
            GuideMutations.AddLocalDelete(
                "workspace.instructions.claude",
                claudePath,
                $"Remove the guide-owned {CLAUDE_IMPORT_LINE} import by deleting the emptied {CLAUDE_FILE_NAME}.",
                mutations);
            return true;
        }

        GuideMutations.AddLocalWrite(
            "workspace.instructions.claude",
            claudePath,
            GuidePlanning.Utf8(claudeAfter),
            $"Remove the guide-owned {CLAUDE_IMPORT_LINE} import from {CLAUDE_FILE_NAME}.",
            mutations);
        return true;
    }

    private bool ValidateClaudeInstructions(string text, ICollection<GuideCheck> checks)
    {
        if (GuideInstructionBlock.State(text, Instructions.Markers) == GuideInstructionBlockStatus.Malformed)
        {
            checks.Add(Check("workspace.instructions.claude", GuideCheckStatus.Error,
                $"Root {CLAUDE_FILE_NAME} contains malformed or duplicate product guide markers."));
            return false;
        }
        if (GuideInstructionBlock.ClaudeImportState(text) == GuideClaudeImportStatus.Duplicate)
        {
            checks.Add(Check("workspace.claude-import", GuideCheckStatus.Error,
                $"Refusing to modify {CLAUDE_FILE_NAME} because it contains duplicate {CLAUDE_IMPORT_LINE} imports."));
            return false;
        }
        return true;
    }

    private bool ConvergeClaudeInstructions(
        string claudePath, string text, bool wantsClaude, bool managedImport, List<GuidePlannedMutation> mutations)
    {
        // This product's current block lives in AGENTS.md; a legacy Claude copy must not
        // override it. Other products' spans and all user-owned content stay untouched.
        var after = GuideInstructionBlock.Remove(text, Instructions.Markers);
        var removedLegacySpan = !string.Equals(text, after, StringComparison.Ordinal);
        after = wantsClaude
            ? GuideInstructionBlock.EnsureClaudeImport(after)
            : managedImport ? GuideInstructionBlock.RemoveClaudeImport(after) : after;
        if (removedLegacySpan)
        {
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            after = after.TrimEnd('\r', '\n');
            if (after.Length > 0) after += newline;
        }
        if (string.Equals(text, after, StringComparison.Ordinal)) return false;
        if (!wantsClaude && managedImport) return AddClaudeImportRemoval(claudePath, after, mutations);
        GuideMutations.AddLocalWrite(
            "workspace.instructions.claude", claudePath, GuidePlanning.Utf8(after),
            $"Remove this product's legacy instruction span and converge the minimal {CLAUDE_IMPORT_LINE} import in {CLAUDE_FILE_NAME}.",
            mutations);
        return true;
    }

    /// <summary>
    /// Shared preview/apply tail: digest gate, blocker gate, no-op gate, per-file drift gate,
    /// then the local file mutations under the engine lock.
    /// </summary>
    private async Task<GuideReport> CompleteAsync(
        GuidePreparedPlan prepared,
        IReadOnlyList<GuideCheck> blockers,
        string? expectedDigest,
        IProgress<GuidePhase>? progress,
        CancellationToken cancellationToken)
    {
        var operation = prepared.PublicPlan.Operation;
        var checks = prepared.PublicPlan.Checks.ToList();
        if (expectedDigest is null)
        {
            return Report(checks, "Preview generated; no changes were made.",
                ["Review the plan and repeat with --apply --plan-digest."], prepared.PublicPlan);
        }

        if (expectedDigest != prepared.PublicPlan.PlanDigest)
        {
            checks.Add(Check("plan.digest", GuideCheckStatus.Error, "Approved plan digest is stale or missing.",
                "Preview and approve current state."));
            return Report(checks, "No changes applied.", ["Preview again."], prepared.PublicPlan);
        }

        if (blockers.Count > 0)
        {
            return Report(checks, "Blockers prevented apply.", ["Resolve errors and preview again."], prepared.PublicPlan);
        }

        if (prepared.PublicPlan.IsNoOp)
        {
            checks.Add(Check("plan.applied", GuideCheckStatus.Ok, "The approved plan was already satisfied; no changes were needed."));
            return Report(checks, "Nothing to apply.", [], prepared.PublicPlan with { Applied = true });
        }

        progress?.Report(new GuidePhase(
            "apply.step",
            $"Applying {prepared.Mutations.Count} workspace change(s)…",
            1,
            prepared.Mutations.Count));
        foreach (var mutation in prepared.Mutations)
        {
            switch (mutation)
            {
                case GuideLocalFileMutation local:
                    await GuideMutations.ApplyLocalFileAsync(local, cancellationToken);
                    break;
                case GuideProjectTreeRemove remove:
                    GuideMutations.ApplyProjectTreeRemove(remove);
                    break;
                case GuideDirectoryCleanup cleanup:
                    GuideMutations.ApplyDirectoryCleanup(cleanup);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported workspace mutation: {mutation.GetType().Name}");
            }
        }

        checks.Add(Check("plan.applied", GuideCheckStatus.Ok, "The approved plan was applied."));
        return Report(checks, "Approved plan applied.",
            ["Install or refresh the profile skills with: guide configure --workspace <path>."],
            prepared.PublicPlan with { Applied = true });
    }

    // ---------------------------------------------------------------------------------------------
    // Workspace detection
    // ---------------------------------------------------------------------------------------------

    internal sealed record GuideWorkspaceDetection(
        GuideWorkspaceDetectionOutcome Outcome,
        string? Identity,
        string? GitRoot,
        string? CandidateProfile,
        string Confidence,
        string Reason,
        IReadOnlyList<string> Capabilities,
        bool HasMonicaProjectReference,
        string? FrameworkVersion,
        string? FrameworkVersionTier,
        IReadOnlyList<string> FrameworkVersionIssues,
        IReadOnlyList<string> NestedInstructionFiles);

    private GuideWorkspaceDetection Detect(string root)
    {
        // Detection runs on every wizard interaction with a candidate path; the identity
        // probe skips commit resolution and the worktree status scan, which can take
        // seconds on large checkouts and whose facts detection never uses.
        var git = _git.FindIdentity(root);
        var identity = git?.CanonicalRemote;
        var isMonicaFramework = File.Exists(Path.Combine(root, "Monica.slnx"))
                                && Directory.Exists(Path.Combine(root, "Monica.Core"));

        // Framework identity and root markers settle the outcome without a file walk, which can take
        // minutes on a framework checkout. Ordinary workspaces use the characteristic scan below.
        if (IsCanonical(identity, "Tairitsua/Monica") || isMonicaFramework)
        {
            return FrameworkRepositoryDetection(
                root,
                git,
                IsCanonical(identity, "Tairitsua/Monica") ? "canonical" : "characteristic");
        }

        var inventory = WalkFiles(root).ToArray();
        var projectFiles = inventory.Where(IsProjectFile).ToArray();
        var projectText = string.Join("\n", projectFiles.Select(ReadText));
        var sourceFiles = inventory
            .Where(static file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !IsProjectFile(file))
            .ToArray();
        var sourceScan = ScanSourceContent(root, sourceFiles);
        var detectionText = $"{projectText}\n{sourceScan.Text}";
        var portablePaths = inventory.Select(file => PortableRelative(root, file)).ToArray();

        var hasUi = inventory.Any(static file => file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                    || UiPattern().IsMatch(detectionText);
        var hasExtension = ExtensionPattern().IsMatch(detectionText)
                           || inventory.Any(static file => ExtensionNamePattern().IsMatch(Path.GetFileName(file)));
        var hasApplication = ApplicationPattern().IsMatch(projectText);
        var hasProjectReference = ProjectReferencePattern().IsMatch(projectText);
        var hasMicroservice = portablePaths.Any(static file => MicroservicePathPattern().IsMatch(file));
        var hasModularMonolith = portablePaths.Any(static file => ModularMonolithPathPattern().IsMatch(file));

        GuideWorkspaceDetectionOutcome outcome;
        string? candidate;
        string confidence;
        string reason;
        if (hasExtension && hasApplication)
        {
            outcome = GuideWorkspaceDetectionOutcome.AmbiguousApplicationAndExtension;
            candidate = null;
            confidence = "ambiguous";
            reason = "Both application and extension characteristics were detected; select a profile explicitly.";
        }
        else if (hasExtension)
        {
            outcome = GuideWorkspaceDetectionOutcome.ExtensionCharacteristics;
            candidate = PROFILE_EXTENSION;
            confidence = "characteristic";
            reason = "Monica extension characteristics detected.";
        }
        else if (hasApplication && sourceScan.Truncated)
        {
            outcome = GuideWorkspaceDetectionOutcome.AmbiguousApplicationUnscanned;
            candidate = null;
            confidence = "ambiguous";
            reason = "Application characteristics were detected, but bounded source scanning could not rule out an extension; select a profile explicitly.";
        }
        else if (hasApplication)
        {
            outcome = GuideWorkspaceDetectionOutcome.ApplicationCharacteristics;
            candidate = PROFILE_APPLICATION;
            confidence = "characteristic";
            reason = "Monica package or project references detected.";
        }
        else if (sourceScan.Truncated)
        {
            outcome = GuideWorkspaceDetectionOutcome.AmbiguousScanExhausted;
            candidate = null;
            confidence = "ambiguous";
            reason = "No decisive characteristics were found before the bounded source scan was exhausted; select a profile explicitly.";
        }
        else
        {
            outcome = GuideWorkspaceDetectionOutcome.AmbiguousNoCharacteristics;
            candidate = null;
            confidence = "ambiguous";
            reason = "No canonical repository identity or characteristic files were found.";
        }

        var capabilities = new List<string>();
        if (hasMicroservice)
        {
            capabilities.Add(CAPABILITY_MICROSERVICE);
        }

        if (hasModularMonolith)
        {
            capabilities.Add(CAPABILITY_MODULAR_MONOLITH);
        }

        if (hasUi)
        {
            capabilities.Add(CAPABILITY_UI);
        }

        var (version, tier, issues) = DetectFrameworkVersion(root, git, projectText, inventory);
        var nested = inventory
            .Where(static file => Path.GetFileName(file) is AGENTS_FILE_NAME or CLAUDE_FILE_NAME)
            .Select(file => PortableRelative(root, file))
            .Where(static relative => relative.Contains('/'))
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();
        return new GuideWorkspaceDetection(
            outcome,
            identity,
            git?.Root,
            candidate,
            confidence,
            reason,
            capabilities,
            hasProjectReference,
            version,
            tier,
            issues,
            nested);
    }

    /// <summary>
    /// Detection for the canonical framework repository without the characteristic scan:
    /// only its version, UI capability, and nested agent instruction files remain relevant.
    /// </summary>
    private static GuideWorkspaceDetection FrameworkRepositoryDetection(
        string root,
        GuideGitIdentity? git,
        string confidence)
    {
        // The framework's own UI project is the only capability its conditional skills need.
        string[] capabilities = Directory.Exists(Path.Combine(root, "Monica.UI"))
            ? [CAPABILITY_UI]
            : Array.Empty<string>();
        var rootVersion = FrameworkRootVersion(git?.Root ?? root);
        return new GuideWorkspaceDetection(
            GuideWorkspaceDetectionOutcome.FrameworkRepository,
            git?.CanonicalRemote,
            git?.Root,
            PROFILE_FRAMEWORK,
            confidence,
            "Monica framework repository detected.",
            capabilities,
            HasMonicaProjectReference: false,
            rootVersion,
            rootVersion is null ? null : "framework-root",
            FrameworkVersionIssues: [],
            NestedAgentFiles(root));
    }

    /// <summary>Names-only walk collecting nested agent instruction files; no content is read.</summary>
    private static string[] NestedAgentFiles(string root)
    {
        var nested = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var directory = queue.Dequeue();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!IGNORED_DIRECTORIES.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                {
                    queue.Enqueue(child);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (Path.GetFileName(file) is AGENTS_FILE_NAME or CLAUDE_FILE_NAME)
                {
                    var relative = PortableRelative(root, file);
                    if (relative.Contains('/'))
                    {
                        nested.Add(relative);
                    }
                }
            }
        }

        return nested.OrderBy(static item => item, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Resolves the effective Monica framework version of the workspace with the tier ladder
    /// of the retired Node engine: project-reference root, resolved lock/assets, central
    /// package management, then project declarations. Only informational here; release and
    /// source parity live with configure and source bindings.
    /// </summary>
    private static (string? Version, string? Tier, IReadOnlyList<string> Issues) DetectFrameworkVersion(
        string root,
        GuideGitIdentity? git,
        string projectText,
        IReadOnlyList<string> inventory)
    {
        var issues = new List<string>();
        var hasProjectReferences = ProjectReferencePattern().IsMatch(projectText);
        if (hasProjectReferences && git is not null && IsCanonical(git.CanonicalRemote, "Tairitsua/Monica"))
        {
            var rootVersion = FrameworkRootVersion(git.Root);
            if (rootVersion is not null)
            {
                return (rootVersion, "framework-root", issues);
            }
        }

        var resolved = inventory
            .Where(static file => Path.GetFileName(file) is "packages.lock.json" or "project.assets.json")
            .SelectMany(ResolvedPackageVersions)
            .ToList();
        if (resolved.Count > 0)
        {
            return (DistinctVersions(resolved, "resolved assets", issues), "resolved-assets", issues);
        }

        var central = inventory
            .Where(static file => Path.GetFileName(file) == "Directory.Packages.props")
            .Select(file => (File: file, Versions: PackageVersionsFromProps(ReadText(file))))
            .ToList();
        var centralVersions = central.SelectMany(static item => item.Versions.Select(static entry => entry.Version)).ToList();
        if (centralVersions.Count > 0 && !hasProjectReferences)
        {
            return (DistinctVersions(centralVersions, "central package management", issues), "central-package-management", issues);
        }

        var centralByName = central.SelectMany(static item => item.Versions).ToDictionary(static entry => entry.Name, static entry => entry.Version, StringComparer.OrdinalIgnoreCase);
        var declared = DeclaredPackageVersions(projectText, centralByName);
        if (declared.Count > 0)
        {
            return (DistinctVersions(declared, "project declaration", issues), "project-declaration", issues);
        }

        if (centralVersions.Count > 0)
        {
            return (DistinctVersions(centralVersions, "central package management", issues), "central-package-management", issues);
        }

        return (null, null, issues);
    }

    private static string? FrameworkRootVersion(string repositoryRoot)
    {
        var file = Path.Combine(repositoryRoot, "Directory.Build.props");
        if (!File.Exists(file))
        {
            return null;
        }

        var match = Regex.Match(ReadText(file), @"<Version>\s*([^<]+?)\s*</Version>", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static IReadOnlyList<(string Name, string Version)> PackageVersionsFromProps(string text)
    {
        var properties = CollectProperties(text);
        var versions = new List<(string Name, string Version)>();
        foreach (var match in ItemElementPattern("PackageVersion").Matches(text).Cast<Match>())
        {
            var attributes = ParseAttributes(match.Groups[1].Value);
            var name = attributes.GetValueOrDefault("Include") ?? attributes.GetValueOrDefault("Update");
            if (name is null || !IsMonicaPackage(name))
            {
                continue;
            }

            var value = attributes.GetValueOrDefault("Version")
                        ?? Regex.Match(match.Value, @"<Version>\s*([^<]+)<\/Version>", RegexOptions.IgnoreCase).Groups[1].Value;
            versions.Add((name, ResolvePropertyVersion(value, properties)));
        }

        return versions;
    }

    private static IReadOnlyList<string> DeclaredPackageVersions(
        string projectText,
        IReadOnlyDictionary<string, string> centralByName)
    {
        var declared = new List<string>();
        foreach (var match in ItemElementPattern("PackageReference").Matches(projectText).Cast<Match>())
        {
            var attributes = ParseAttributes(match.Groups[1].Value);
            var name = attributes.GetValueOrDefault("Include") ?? attributes.GetValueOrDefault("Update");
            if (name is null || !IsMonicaPackage(name))
            {
                continue;
            }

            var value = attributes.GetValueOrDefault("VersionOverride") ?? attributes.GetValueOrDefault("Version");
            if (value is { Length: > 0 })
            {
                declared.Add(ResolvePropertyVersion(value, CollectProperties(match.Value)));
            }
            else if (centralByName.TryGetValue(name, out var centralVersion))
            {
                declared.Add(centralVersion);
            }
        }

        return declared;
    }

    private static IEnumerable<string> ResolvedPackageVersions(string file)
    {
        var text = ReadText(file);
        return Path.GetFileName(file) == "project.assets.json"
            ? AssetLibraryPattern().Matches(text).Cast<Match>()
                .Where(static match => IsMonicaPackage(match.Groups[1].Value))
                .Select(static match => match.Groups[2].Value)
            : LockDependencyPattern().Matches(text).Cast<Match>()
                .Where(static match => IsMonicaPackage(match.Groups[1].Value))
                .Select(static match => match.Groups[2].Value);
    }

    private static string DistinctVersions(IReadOnlyList<string> versions, string tier, List<string> issues)
    {
        var distinct = versions.Distinct(StringComparer.Ordinal).OrderBy(static item => item, StringComparer.Ordinal).ToArray();
        if (distinct.Length > 1)
        {
            issues.Add($"Mixed {tier} versions detected: {string.Join(", ", distinct)}.");
        }

        return distinct[0];
    }

    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var match in AttributePattern().Matches(text).Cast<Match>())
        {
            attributes[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return attributes;
    }

    private static Dictionary<string, string> CollectProperties(string text)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var match in PropertyPattern().Matches(text).Cast<Match>())
        {
            properties[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        }

        return properties;
    }

    private static string ResolvePropertyVersion(string value, Dictionary<string, string> properties)
    {
        var resolved = value.Trim();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (resolved.Length > 0)
        {
            var match = Regex.Match(resolved, @"^\$\(([A-Za-z_][\w.-]*)\)$");
            if (!match.Success)
            {
                break;
            }

            var property = match.Groups[1].Value;
            if (!properties.TryGetValue(property, out var next) || !visited.Add(property))
            {
                return resolved;
            }

            resolved = next.Trim();
        }

        return resolved;
    }

    private static bool IsMonicaPackage(string name)
        => MonicaPackagePattern().IsMatch(name);

    private static bool IsCanonical(string? identity, string canonical)
        => string.Equals(identity, canonical, StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectFile(string file)
        => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
           || file.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
           || file.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
           || file.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownCapability(string capability)
        => capability is CAPABILITY_MICROSERVICE or CAPABILITY_MODULAR_MONOLITH or CAPABILITY_UI;

    private static IEnumerable<string> WalkFiles(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var directory = queue.Dequeue();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!IGNORED_DIRECTORIES.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                {
                    queue.Enqueue(child);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (IsProjectFile(file)
                    || name is AGENTS_FILE_NAME or CLAUDE_FILE_NAME
                    || name == "packages.lock.json"
                    || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    private static (string Text, bool Truncated) ScanSourceContent(string root, IReadOnlyList<string> files)
    {
        var snippets = new List<string>();
        long scannedBytes = 0;
        var truncated = false;
        var ordered = files
            .Select(file => (File: file, Priority: SourcePriority(Path.GetFileName(file))))
            .OrderBy(static item => item.Priority)
            .ThenBy(item => PortableName(root, item.File), StringComparer.Ordinal)
            .Take(SOURCE_SCAN_FILE_LIMIT);
        foreach (var item in ordered)
        {
            if (scannedBytes >= SOURCE_SCAN_BYTE_LIMIT)
            {
                truncated = true;
                break;
            }

            try
            {
                using var stream = File.OpenRead(item.File);
                var remaining = SOURCE_SCAN_BYTE_LIMIT - scannedBytes;
                var length = (int)Math.Min(stream.Length, remaining);
                var buffer = new byte[length];
                var read = stream.Read(buffer, 0, length);
                snippets.Add(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
                scannedBytes += read;
                truncated |= read < stream.Length;
            }
            catch (IOException)
            {
                truncated = true;
            }
        }

        truncated |= snippets.Count < files.Count;
        return (string.Join("\n", snippets), truncated);
    }

    private static int SourcePriority(string fileName)
        => ModuleFileNamePattern().IsMatch(fileName) ? 0
            : ExtensionNamePattern().IsMatch(fileName) ? 1
            : 2;

    private static string PortableName(string root, string file)
        => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string PortableRelative(string root, string file)
        => PortableName(root, file);

    // ---------------------------------------------------------------------------------------------
    // Configuration, instructions, and profile contracts
    // ---------------------------------------------------------------------------------------------

    // Workspace configuration lives in the shared store; this service adds plan semantics.
    private static GuideWorkspaceStore.GuideWorkspaceConfig? LoadConfig(string root, ICollection<GuideCheck> checks)
    {
        var config = GuideWorkspaceStore.LoadConfig(root, out var issue);
        if (issue is not null)
        {
            checks.Add(Check("workspace.config", GuideCheckStatus.Warning, issue,
                "Run guide init --profile <profile> and approve the plan to rewrite it."));
        }

        return config;
    }

    private string RenderInstructions(string profile, IReadOnlyCollection<string>? disabledRules = null)
    {
        var template = Instructions.Templates.GetValueOrDefault(profile)
                       ?? throw new InvalidDataException($"Catalog does not define instructions for profile '{profile}'.");
        var skills = string.Join(", ", template.Skills.Select(static name => $"${name}"));
        var rules = template.Rules
            .Where(rule => disabledRules?.Contains(rule.Id) != true)
            .Select(static rule => $"- {rule.Text}")
            .ToArray();
        var ruleList = rules.Length == 0 ? string.Empty : $"\n\n{string.Join("\n", rules)}";
        return $"## Monica agent workflow\n\nProfile skills: {skills}.{ruleList}{ProjectedTail()}";
    }

    private string? _projectedTail;

    /// <summary>
    /// Machine-state sections appended to the managed block of the global-agent-policy owner
    /// only: verified first-party source locators and the issue-reporting policy, each gated
    /// by its machine-global switch. Other products render catalog data alone so a workspace
    /// guided by several products carries these sections at most once. Cached per service
    /// instance because binding observation probes Git once, not once per rendered workspace.
    /// </summary>
    private string ProjectedTail()
    {
        if (_projectedTail is not null)
        {
            return _projectedTail;
        }

        _projectedTail = _definition.OwnsGlobalAgentPolicy ? BuildProjectedTail() : string.Empty;
        return _projectedTail;
    }

    private string BuildProjectedTail()
    {
        var sections = new List<string>();
        var projection = GuideWorkspaceProjectionStore.Load(_enginePaths);
        if (projection.SourceHints)
        {
            var bound = new GuideSourceService(_enginePaths, _catalog).Describe()
                .Where(static status => status.Binding is not null)
                .OrderBy(static status => status.Repository, StringComparer.Ordinal)
                .ToArray();
            if (bound.Length > 0)
            {
                var lines = bound
                    .Select(static status =>

                        // The stored binding carries exact provenance; live health stays a
                        // lookup-time concern so the block does not churn with every checkout.
                        $"- {status.Binding!.Repository} verified checkout (lookup only, never write permission): {status.Binding.SourcePath} (commit {(status.Binding.Commit.Length > 12 ? status.Binding.Commit[..12] : status.Binding.Commit)}).")
                    .ToList();
                lines.Add("- Bindings can move or go stale; re-verify with the guide's `source resolve` before relying on a path.");
                sections.Add($"## First-party source on this machine\n\n{string.Join("\n", lines)}");
            }
        }

        if (projection.IssuePolicy)
        {
            var mode = GuideIssuePreferencesStore.Load(_enginePaths).IssueReporting;
            var modeName = mode.ToString().ToLowerInvariant();
            sections.Add(
                "## Issue reporting policy\n\n" +
                $"- Machine policy is '{modeName}': {mode.Describe()}\n" +
                "- A persisted policy never authorizes a remote action; current-session approval names the exact action and target.");
        }

        return sections.Count == 0 ? string.Empty : $"\n\n{string.Join("\n\n", sections)}";
    }

    /// <summary>Whether the managed body equals the currently rendered instructions, machine projection included.</summary>
    private bool InstructionsMatch(
        string agentsText,
        string profile,
        GuideInstructionMarkers markers,
        IReadOnlyCollection<string>? disabledRules = null)
    {
        var expected = $"\n{RenderInstructions(profile, disabledRules).Trim()}\n";
        var actual = GuideInstructionBlock.NormalizeNewlines(
            GuideInstructionBlock.Body(agentsText, markers) ?? string.Empty);
        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    /// <summary>The catalog required by init, inspect, and forget; registry-only surfaces tolerate a null catalog.</summary>
    private SkillCatalog Catalog
        => _catalog ?? throw new InvalidOperationException(
            "This workspace operation requires the product's installed skill catalog.");

    /// <summary>The managed instruction contract those flows render and verify.</summary>
    private GuideManagedInstructions Instructions
        => Catalog.ManagedInstructions
           ?? throw new InvalidOperationException(
               "The installed catalog declares no workspace profiles; install a current release bundle.");

    private bool WantsClaudeImport(GuideWorkspaceStore.GuideWorkspaceConfig? config = null)
    {
        if (config?.EffectiveSkillTargets().Any(static target =>
                string.Equals(target.Replace('\\', '/').TrimEnd('/'), ".claude/skills", StringComparison.OrdinalIgnoreCase)) == true)
            return true;
        if (!File.Exists(_enginePaths.GuideLedgerFile))
        {
            return false;
        }

        try
        {
            var ledger = JsonSerializer.Deserialize<GuideLedger>(
                File.ReadAllBytes(_enginePaths.GuideLedgerFile), GuidePlanning.JsonOptions);
            return ledger?.Products
                       .FirstOrDefault(product => string.Equals(product.ProductId, _definition.ProductId, StringComparison.Ordinal))
                       ?.Installations.Any(static installation => installation.Target == GuideTarget.Claude)
                   ?? false;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<GuideCheck> ProfileRepositoryIssues(
        string root,
        GuideWorkspaceDetection detection,
        string? profile)
    {
        var issues = new List<GuideCheck>();
        if (profile == PROFILE_FRAMEWORK)
        {
            AddRepositoryContractIssues(root, detection, issues, "Tairitsua/Monica", PROFILE_FRAMEWORK);
        }

        return issues;
    }

    private static void AddRepositoryContractIssues(
        string root,
        GuideWorkspaceDetection detection,
        List<GuideCheck> issues,
        string canonical,
        string profile)
    {
        if (!IsCanonical(detection.Identity, canonical))
        {
            issues.Add(Check("workspace.repository", GuideCheckStatus.Error,
                $"{profile} requires a canonical {canonical} checkout through origin or upstream."));
        }

        var gitRoot = detection.GitRoot is null ? null : Path.GetFullPath(detection.GitRoot);
        if (gitRoot is null || !string.Equals(Path.GetFullPath(root), gitRoot, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Check("workspace.repository", GuideCheckStatus.Error,
                $"{profile} must select the canonical Git repository root.",
                gitRoot is null ? null : $"Select: {gitRoot}"));
        }
    }

    private void InspectProfile(
        string root,
        GuideWorkspaceDetection detection,
        GuideWorkspaceStore.GuideWorkspaceConfig? config,
        List<GuideCheck> checks)
    {
        checks.Add(detection.Identity is null
            ? Check("workspace.repository", GuideCheckStatus.Warning, detection.Reason,
                "Choose a profile explicitly; the guide will not guess for a non-Monica repository.")
            : Check("workspace.repository", GuideCheckStatus.Ok, $"{detection.Identity}: {detection.Reason}"));

        if (detection.FrameworkVersion is not null)
        {
            checks.Add(Check("workspace.framework", GuideCheckStatus.Ok,
                $"Resolved {_definition.DisplayName} {detection.FrameworkVersion} from {detection.FrameworkVersionTier}."));
        }
        else if (detection.FrameworkVersionIssues.Count > 0)
        {
            foreach (var issue in detection.FrameworkVersionIssues)
            {
                checks.Add(Check("workspace.framework", GuideCheckStatus.Warning, issue));
            }
        }

        if (config is null)
        {
            checks.Add(Check("workspace.profile", GuideCheckStatus.Warning,
                "Repository has not been initialized by the guide; this is valid until a workflow is chosen."));
            return;
        }

        var knownProfile = Instructions.Templates.ContainsKey(config.Profile);
        checks.Add(knownProfile
            ? Check("workspace.profile", GuideCheckStatus.Ok,
                $"Profile {config.Profile} is confirmed (capabilities: {string.Join(", ", config.Capabilities)}).")
            : Check("workspace.profile", GuideCheckStatus.Error,
                $"Configured profile '{config.Profile}' is not defined by the installed catalog.",
                "Run guide init with a current profile."));
        checks.AddRange(ProfileRepositoryIssues(root, detection, knownProfile ? config.Profile : null));
        if (knownProfile && config.Profile == PROFILE_EXTENSION && detection.HasMonicaProjectReference)
        {
            checks.Add(Check("workspace.project-reference", GuideCheckStatus.Error,
                "Extension projects must consume Monica through immutable NuGet packages; a Monica ProjectReference was detected."));
        }
    }

    private void InspectInstructions(string root, GuideWorkspaceStore.GuideWorkspaceConfig? config, List<GuideCheck> checks)
    {
        var markers = Instructions.Markers;
        var agentsPath = Path.Combine(root, AGENTS_FILE_NAME);
        var agentsText = ReadText(agentsPath);
        switch (GuideInstructionBlock.State(agentsText, markers))
        {
            case GuideInstructionBlockStatus.Malformed:
                checks.Add(Check("workspace.instructions", GuideCheckStatus.Error,
                    $"Root {AGENTS_FILE_NAME} contains malformed or duplicate guide markers."));
                break;
            case GuideInstructionBlockStatus.Absent when config is not null:
                checks.Add(Check("workspace.instructions", GuideCheckStatus.Error,
                    "Configured repository is missing the root managed instruction block.",
                    "Run guide init again and approve the plan."));
                break;
            case GuideInstructionBlockStatus.Valid when config is not null:
                checks.Add(InstructionsMatch(agentsText, config.Profile, markers, config.DisabledRules)
                    ? Check("workspace.instructions", GuideCheckStatus.Ok,
                        $"Root {AGENTS_FILE_NAME} has one current managed instruction block.")
                    : Check("workspace.instructions", GuideCheckStatus.Error,
                        $"Root {AGENTS_FILE_NAME} managed body does not match the configured profile template or machine state.",
                        "Run guide init again and approve the plan, or update the workspace to converge it."));
                if (RegisteredEntry(GuideWorkspaceRegistryFile.Load(_enginePaths), root)?.InstructionBlockVersion
                    is { } registeredVersion && registeredVersion != Instructions.Version)
                {
                    checks.Add(Check("workspace.instructions", GuideCheckStatus.Warning,
                        $"Registered instruction block version {registeredVersion} does not match catalog version {Instructions.Version}.",
                        "Run guide init again and approve the plan."));
                }

                break;
        }

        var claudeText = ReadText(Path.Combine(root, CLAUDE_FILE_NAME));
        var claudeState = GuideInstructionBlock.ClaudeImportState(claudeText);
        var claudeRequired = WantsClaudeImport(config);
        var claudeProductSpan = GuideInstructionBlock.State(claudeText, Instructions.Markers);
        if (claudeProductSpan != GuideInstructionBlockStatus.Absent)
            checks.Add(Check("workspace.instructions.claude",
                claudeProductSpan == GuideInstructionBlockStatus.Malformed ? GuideCheckStatus.Error : GuideCheckStatus.Warning,
                $"Root {CLAUDE_FILE_NAME} carries a legacy product instruction span.",
                "Run guide configure for this workspace; malformed markers need correction before apply."));
        if (claudeState == GuideClaudeImportStatus.Duplicate)
        {
            checks.Add(Check("workspace.claude-import", GuideCheckStatus.Error,
                $"Root {CLAUDE_FILE_NAME} contains duplicate {CLAUDE_IMPORT_LINE} imports."));
        }
        else if (config is not null && claudeRequired && claudeState != GuideClaudeImportStatus.Valid)
        {
            checks.Add(Check("workspace.claude-import", GuideCheckStatus.Error,
                $"A Claude target is installed but root {CLAUDE_FILE_NAME} does not import {CLAUDE_IMPORT_LINE}.",
                "Run guide init again and approve the plan."));
        }
        else if (config is not null && !claudeRequired && config.ManagedClaudeImport
                 && claudeState == GuideClaudeImportStatus.Valid)
        {
            checks.Add(Check("workspace.claude-import", GuideCheckStatus.Warning,
                $"Guide-owned root {CLAUDE_IMPORT_LINE} import remains although no Claude target is installed.",
                "Run guide init again and approve the plan."));
        }
    }

    private string ProfileList()
        => string.Join(", ", Instructions.Templates.Keys.OrderBy(static item => item, StringComparer.Ordinal));

    private static string ReadText(string path)
        => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    private static GuideCheck Check(string id, GuideCheckStatus status, string message, string? remediation = null,
        IReadOnlyDictionary<string, string>? details = null)
        => new(id, status, message, remediation, details);

    private static GuideReport Report(IReadOnlyList<GuideCheck> checks, string message,
        IReadOnlyList<string> nextActions, GuidePlan? plan = null)
        => new(GuideContractVersions.CURRENT, AgentGuideInfo.CurrentVersion(), StatusOf(checks), checks,
            new GuideSummary("workspace", message, DateTimeOffset.UtcNow, nextActions), plan);

    private static GuideStatus StatusOf(IEnumerable<GuideCheck> checks)
    {
        var statuses = checks.Select(static item => item.Status).ToArray();
        return statuses.Contains(GuideCheckStatus.Error) ? GuideStatus.Error
            : statuses.Contains(GuideCheckStatus.Warning) ? GuideStatus.Warning : GuideStatus.Ready;
    }

    [GeneratedRegex(@"Monica\.[\w.]*UI|MudBlazor", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UiPattern();

    [GeneratedRegex(@"monica-third-party|ModuleRegistration\s*<", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();

    [GeneratedRegex(@"Extension|Provider|Connector", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionNamePattern();

    [GeneratedRegex(@"^Module.*\.cs$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModuleFileNamePattern();

    [GeneratedRegex(@"PackageReference[^>]+Include=[""']Monica\.|ProjectReference[^>]+Monica\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationPattern();

    [GeneratedRegex(@"ProjectReference[^>]+(?:Include|Update)=[""'][^""']*Monica", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectReferencePattern();

    [GeneratedRegex(@"^src/Services/|[^/]+Service\.(?:API|Domain)\.(?:csproj|fsproj)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MicroservicePathPattern();

    [GeneratedRegex(@"^src/Domains/|Domains\.[^/]+\.(?:csproj|fsproj)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModularMonolithPathPattern();

    [GeneratedRegex(@"^Monica(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonicaPackagePattern();

    [GeneratedRegex(@"""(Monica[^/""]*)""\s*:\s*\{[^{}]*?""resolved""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LockDependencyPattern();

    [GeneratedRegex(@"""(Monica[^/""]+)/([^/""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssetLibraryPattern();

    [GeneratedRegex(@"([\w:.-]+)\s*=\s*[""']([^""']*)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"<([A-Za-z_][\w.-]*)>\s*([^<]+?)\s*</\1>", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyPattern();

    private static Regex ItemElementPattern(string tagName)
        => new($"<{tagName}\\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>Status of the guide-owned instruction span inside one instruction file.</summary>
internal enum GuideInstructionBlockStatus
{
    Absent,
    Valid,
    Malformed
}

/// <summary>Status of the <c>@AGENTS.md</c> import inside one Claude instruction file.</summary>
internal enum GuideClaudeImportStatus
{
    Absent,
    Valid,
    Duplicate
}

/// <summary>
/// Marker-delimited instruction span handling with semantics ported from the retired Node
/// engine: markers must occupy whole lines exactly once, bodies keep the file's newline
/// style, and appends leave one blank line between existing content and the new span.
/// </summary>
internal static class GuideInstructionBlock
{
    internal static GuideInstructionBlockStatus State(string text, GuideInstructionMarkers markers)
    {
        var starts = AllIndices(text, markers.Start);
        var ends = AllIndices(text, markers.End);
        if (starts.Count == 0 && ends.Count == 0)
        {
            return GuideInstructionBlockStatus.Absent;
        }

        if (starts.Count != 1 || ends.Count != 1 || starts[0] >= ends[0]
            || !OccupiesLine(text, starts[0], markers.Start.Length)
            || !OccupiesLine(text, ends[0], markers.End.Length))
        {
            return GuideInstructionBlockStatus.Malformed;
        }

        return GuideInstructionBlockStatus.Valid;
    }

    internal static string? Body(string text, GuideInstructionMarkers markers)
    {
        var start = text.IndexOf(markers.Start, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var bodyStart = start + markers.Start.Length;
        var end = text.IndexOf(markers.End, bodyStart, StringComparison.Ordinal);
        return end < 0 ? null : text[bodyStart..end];
    }

    internal static string Upsert(string text, string body, GuideInstructionMarkers markers)
    {
        var state = State(text, markers);
        if (state == GuideInstructionBlockStatus.Malformed)
        {
            throw new InvalidOperationException("Refusing to modify malformed or duplicate guide markers.");
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalizedBody = body.Trim().Replace("\r\n", "\n", StringComparison.Ordinal);
        var renderedBody = normalizedBody.Replace("\n", newline, StringComparison.Ordinal);
        var block = $"{markers.Start}{newline}{renderedBody}{newline}{markers.End}";
        if (state == GuideInstructionBlockStatus.Absent)
        {
            if (text.Length == 0)
            {
                return $"{block}{newline}";
            }

            var separator = text.EndsWith($"{newline}{newline}", StringComparison.Ordinal)
                ? string.Empty
                : text.EndsWith(newline, StringComparison.Ordinal)
                    ? newline
                    : $"{newline}{newline}";
            return $"{text}{separator}{block}{newline}";
        }

        var span = BlockSpan(text, markers);
        return $"{text[..span.Start]}{block}{text[span.End..]}";
    }

    internal static string Remove(string text, GuideInstructionMarkers markers)
    {
        var state = State(text, markers);
        if (state == GuideInstructionBlockStatus.Malformed)
        {
            throw new InvalidOperationException("Refusing to modify malformed or duplicate guide markers.");
        }

        if (state == GuideInstructionBlockStatus.Absent)
        {
            return text;
        }

        var span = BlockSpan(text, markers);
        return $"{text[..span.Start]}{text[span.End..]}";
    }

    internal static GuideClaudeImportStatus ClaudeImportState(string text)
    {
        var count = SplitLines(text).Count(static line => line.Trim() == "@AGENTS.md");
        return count == 0 ? GuideClaudeImportStatus.Absent
            : count == 1 ? GuideClaudeImportStatus.Valid
            : GuideClaudeImportStatus.Duplicate;
    }

    internal static string EnsureClaudeImport(string text)
    {
        var state = ClaudeImportState(text);
        if (state == GuideClaudeImportStatus.Duplicate)
        {
            throw new InvalidOperationException("Refusing to modify CLAUDE.md because it contains duplicate @AGENTS.md imports.");
        }

        if (state == GuideClaudeImportStatus.Valid)
        {
            return text;
        }

        return AppendLine(text, "@AGENTS.md");
    }

    internal static string RemoveClaudeImport(string text)
    {
        var state = ClaudeImportState(text);
        if (state == GuideClaudeImportStatus.Duplicate)
        {
            throw new InvalidOperationException("Refusing to remove duplicated @AGENTS.md imports from CLAUDE.md.");
        }

        if (state == GuideClaudeImportStatus.Absent)
        {
            return text;
        }

        var span = ImportLineSpan(text);
        return $"{text[..span.Start]}{text[span.End..]}";
    }

    internal static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static (int Start, int End) BlockSpan(string text, GuideInstructionMarkers markers)
    {
        var start = text.IndexOf(markers.Start, StringComparison.Ordinal);
        var bodyStart = start + markers.Start.Length;
        var end = text.IndexOf(markers.End, bodyStart, StringComparison.Ordinal) + markers.End.Length;
        return (start, end);
    }

    private static (int Start, int End) ImportLineSpan(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var search = 0;
        while (search < text.Length)
        {
            var lineEnd = text.IndexOf('\n', search);
            var exclusiveEnd = lineEnd < 0 ? text.Length : lineEnd + 1;
            var line = text[search..exclusiveEnd].Trim();
            if (line == "@AGENTS.md")
            {
                return (search, exclusiveEnd);
            }

            search = exclusiveEnd;
        }

        return (text.Length, text.Length);
    }

    private static string AppendLine(string text, string line)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (text.Length == 0)
        {
            return $"{line}{newline}";
        }

        var separator = text.EndsWith($"{newline}{newline}", StringComparison.Ordinal)
            ? string.Empty
            : text.EndsWith(newline, StringComparison.Ordinal)
                ? newline
                : $"{newline}{newline}";
        return $"{text}{separator}{line}{newline}";
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var search = 0;
        while (search < text.Length)
        {
            var lineEnd = text.IndexOf('\n', search);
            var exclusiveEnd = lineEnd < 0 ? text.Length : lineEnd + 1;
            var line = text[search..exclusiveEnd];
            if (line.EndsWith("\r\n", StringComparison.Ordinal))
            {
                line = line[..^2];
            }
            else if (line.EndsWith('\n'))
            {
                line = line[..^1];
            }

            yield return line;
            search = exclusiveEnd;
        }
    }

    private static bool OccupiesLine(string text, int index, int length)
    {
        var before = index == 0 || text[index - 1] == '\n';
        var afterIndex = index + length;
        var after = afterIndex == text.Length
                    || text[afterIndex] == '\n'
                    || (text[afterIndex] == '\r' && afterIndex + 1 < text.Length && text[afterIndex + 1] == '\n');
        return before && after;
    }

    private static List<int> AllIndices(string text, string marker)
    {
        var indices = new List<int>();
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        while (index >= 0)
        {
            indices.Add(index);
            index = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
        }

        return indices;
    }
}
