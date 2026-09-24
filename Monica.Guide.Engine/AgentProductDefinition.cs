namespace Monica.Guide;

/// <summary>
/// Declarative identity of one installable agent product. The guide engine is generic over
/// these definitions: every product-specific expectation (entry executable, routes, hosts,
/// release pins, update feed location) is data on the definition instead of engine logic, so
/// one engine serves every product and a product stays a thin composition layer.
/// </summary>
public sealed record AgentProductDefinition
{
    /// <summary>Stable machine identity; also the manifest <c>productId</c> pin.</summary>
    public required string ProductId { get; init; }

    /// <summary>The manifest <c>productName</c> pin, for example <c>Monica.Workflow</c>.</summary>
    public required string ProductName { get; init; }

    /// <summary>Human-facing product name used in messages and shell integration.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Prefix shared by every catalog-owned skill directory, for example <c>monica-workflow-</c>.
    /// Staging directories and ownership messages use it.
    /// </summary>
    public required string SkillNamespacePrefix { get; init; }

    /// <summary>
    /// Default loopback serve port. Null when the product runs no local server; such products
    /// skip persisted server configuration and runtime endpoint probes entirely.
    /// </summary>
    public int? DefaultPort { get; init; }

    /// <summary>Named routes a release manifest must declare, mapped to absolute paths.</summary>
    public IReadOnlyDictionary<string, string> DefaultRoutes { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Agent hosts a release declares support for, ordered exactly as pinned.</summary>
    public IReadOnlyList<string> SupportedHosts { get; init; } = [];

    /// <summary>
    /// .NET runtime band (major.minor, for example "10.0") a release's SDK must stay inside; null
    /// skips the pin. The SDK patch level is build provenance, not a product contract.
    /// </summary>
    public string? DotnetSdk { get; init; }

    /// <summary>
    /// Native platforms this product publishes, at least one. Every release pins exactly
    /// one entry; executable naming, distribution kind, and archive naming resolve through it.
    /// </summary>
    public required IReadOnlyList<AgentProductPlatform> Platforms { get; init; }

    /// <summary>
    /// Bundle directory that carries the product's program entry: <c>app</c> for products
    /// with a serve application, <c>setup</c> when the guide executable is the product's
    /// only program. Release manifests pin the entry point against this tree.
    /// </summary>
    public string ProgramEntryTree { get; init; } = "app";

    /// <summary>GitHub repository that publishes this product's releases, for the update feed.</summary>
    public string? GitHubSlug { get; init; }

    /// <summary>
    /// Release archive asset name with <c>{version}</c> and <c>{rid}</c> placeholders, for
    /// example <c>monica-workflow-v{version}-{rid}.zip</c>. The update feed resolves the
    /// latest release's archive and checksum assets from it for the running platform.
    /// </summary>
    public string ArchiveAssetNameTemplate { get; init; } = string.Empty;

    /// <summary>
    /// Directory name prefix of extracted release bundles, for example <c>monica-workflow-</c>;
    /// rollback candidate discovery lists siblings with this prefix.
    /// </summary>
    public string BundleDirectoryPrefix { get; init; } = string.Empty;

    /// <summary>MCP server identity a release must declare; null for products without MCP.</summary>
    public string? McpServerName { get; init; }

    /// <summary>MCP endpoint path a release must declare; null for products without MCP.</summary>
    public string? McpPath { get; init; }

    /// <summary>
    /// Read-only MCP tool the doctor onboarding probe calls. Required when the product
    /// declares MCP; unused otherwise.
    /// </summary>
    public string? DoctorReadOnlyTool { get; init; }

    /// <summary>
    /// The product's guide skill: workspace-independent product infrastructure that the
    /// global-first preference keeps out of per-workspace closures and installs once at the
    /// user level instead. Null for products without such a skill.
    /// </summary>
    public string? GlobalGuideSkill { get; init; }

    /// <summary>
    /// Whether this product's guide owns the machine-global agent policy: first-party source
    /// bindings, the issue-reporting preference, and their projection into managed workspace
    /// instructions. Only the owner's wizard and CLI expose the Sources surface and its
    /// switches; every other product renders its managed block from catalog data alone, so a
    /// workspace guided by several products carries the machine-global sections at most once,
    /// inside the owner's block.
    /// </summary>
    public bool OwnsGlobalAgentPolicy { get; init; }

    /// <summary>Named scenario routes a release must declare exactly; null skips the pin.</summary>
    public IReadOnlyList<string>? SupportedRoutes { get; init; }

    /// <summary>Named default skill journeys a release must declare exactly; null skips the pin.</summary>
    public IReadOnlyDictionary<string, string>? DefaultJourneys { get; init; }

    /// <summary>One-line product purpose shown by <c>overview</c>; null uses a generic line.</summary>
    public string? Tagline { get; init; }

    /// <summary>Arguments shortcuts pass to the product executable; null or empty passes none.</summary>
    public string? ShortcutArguments { get; init; }

    /// <summary>
    /// Arguments used when the guide starts the product in the background (install finish,
    /// autostart); null or empty starts the executable without arguments.
    /// </summary>
    public string? BackgroundStartArguments { get; init; }

    /// <summary>
    /// LocalAppData directory name for product-owned state (server configuration, transactions,
    /// logs, installation locator), for example <c>Monica.Workflow</c>. The unified guide
    /// ownership ledger itself lives in the guide engine root instead.
    /// </summary>
    public required string ProductDataRootName { get; init; }

    /// <summary>Whether the product runs a local loopback server and persists a serve port.</summary>
    public bool ServesLoopback => DefaultPort is not null;

    /// <summary>Resolves one platform slice by runtime identifier; null when unlisted.</summary>
    public AgentProductPlatform? PlatformFor(string runtimeIdentifier)
        => Platforms.FirstOrDefault(platform =>
            string.Equals(platform.RuntimeIdentifier, runtimeIdentifier, StringComparison.Ordinal));

    /// <summary>The platform slice matching the running process; null when unlisted.</summary>
    public AgentProductPlatform? CurrentPlatform()
        => PlatformFor(AgentProductPlatform.CurrentRuntimeIdentifier);

    /// <summary>
    /// Relative program entry inside a release bundle of one platform, for example
    /// <c>app/Monica.Workflow</c>; null for skill-only products without a runnable program.
    /// </summary>
    public string? ProgramEntryPointFor(string runtimeIdentifier)
        => PlatformFor(runtimeIdentifier) is { } platform ? $"{ProgramEntryTree}/{platform.ExecutableName}" : null;

    /// <summary>Program entry of the running platform; null when the platform is unlisted.</summary>
    public string? CurrentProgramEntryPoint => ProgramEntryPointFor(AgentProductPlatform.CurrentRuntimeIdentifier);
}
