namespace Monica.Guide;

/// <summary>
/// The registry of products the unified Monica guide can install. One definition per product
/// is the single source of its release contract: the guide application, the product's own CLI
/// adapter, and release validation all read the same instance.
/// </summary>
public static class KnownAgentProducts
{
    /// <summary>
    /// The portable three-platform matrix every product publishes today: Windows, Linux,
    /// and Apple Silicon macOS. The Windows bundle keeps the <c>.exe</c> suffix; the Unix
    /// bundles ship the same executable name without it.
    /// </summary>
    private static IReadOnlyList<AgentProductPlatform> PortablePlatforms(string executableBaseName)
        =>
        [
            new AgentProductPlatform
            {
                RuntimeIdentifier = "win-x64",
                DistributionKind = "portable-win-x64",
                ExecutableName = $"{executableBaseName}.exe"
            },
            new AgentProductPlatform
            {
                RuntimeIdentifier = "linux-x64",
                DistributionKind = "portable-linux-x64",
                ExecutableName = executableBaseName
            },
            new AgentProductPlatform
            {
                RuntimeIdentifier = "osx-arm64",
                DistributionKind = "portable-osx-arm64",
                ExecutableName = executableBaseName
            }
        ];

    /// <summary>
    /// The Monica agent-skill product: the guide executable plus the co-versioned Monica
    /// skill catalog. It runs no local service; the guide executable is the product's only
    /// program, so its bundle ships it as the setup tree.
    /// </summary>
    public static AgentProductDefinition Monica { get; } = new()
    {
        ProductId = "Tairitsua.Monica",
        ProductName = "Monica",
        DisplayName = "Monica",
        SkillNamespacePrefix = "monica-",
        DefaultPort = null,
        SupportedHosts = ["claude", "codex"],
        DotnetSdk = "10.0",
        Platforms = PortablePlatforms("Monica.Guide"),
        ProgramEntryTree = "setup",
        GitHubSlug = "Tairitsua/Monica",
        ArchiveAssetNameTemplate = "monica-guide-v{version}-{rid}.zip",
        BundleDirectoryPrefix = "monica-guide-",
        GlobalGuideSkill = "monica-guide",
        // Monica is the machine's agent-policy owner: its guide alone manages first-party
        // source bindings, the issue-reporting preference, and their instruction projection.
        OwnsGlobalAgentPolicy = true,
        Tagline = "the Monica toolbox: agent skills, framework guidance, and this guide.",
        ProductDataRootName = "Monica",
    };

    /// <summary>
    /// The Monica Workflow product: a loopback cockpit executable, its MCP machine API, and
    /// the co-versioned <c>monica-workflow-*</c> skill catalog.
    /// </summary>
    public static AgentProductDefinition MonicaWorkflow { get; } = new()
    {
        ProductId = "Tairitsua.Monica.Workflow",
        ProductName = "Monica.Workflow",
        DisplayName = "Monica Workflow",
        SkillNamespacePrefix = "monica-workflow-",
        DefaultPort = 61345,
        DefaultRoutes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["health"] = "/healthz",
            ["mcp"] = "/mcp/monica-workflow",
            ["ui"] = "/workflow"
        },
        SupportedHosts = ["claude", "codex"],
        DotnetSdk = "10.0",
        Platforms = PortablePlatforms("Monica.Workflow"),
        GitHubSlug = "Tairitsua/Monica.Workflow",
        ArchiveAssetNameTemplate = "monica-workflow-v{version}-{rid}.zip",
        BundleDirectoryPrefix = "monica-workflow-",
        GlobalGuideSkill = "monica-workflow-guide",
        McpServerName = "monica-workflow",
        McpPath = "/mcp/monica-workflow",
        DoctorReadOnlyTool = "list-document-contracts",
        SupportedRoutes = ["implementation", "investigation", "knowledge"],
        DefaultJourneys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["firstUse"] = "monica-workflow-guide",
            ["governedWork"] = "monica-workflow",
            ["inbox"] = "monica-workflow-scenarios"
        },
        ShortcutArguments = "serve",
        BackgroundStartArguments = "serve --no-open-browser",
        Tagline = "turns reviewed evidence into governed, auditable development work.",
        ProductDataRootName = "Monica.Workflow",
    };

    /// <summary>Resolves one registered definition by its manifest product id; null when unknown.</summary>
    public static AgentProductDefinition? FindByProductId(string productId)
        => All.FirstOrDefault(product =>
            string.Equals(product.ProductId, productId, StringComparison.Ordinal));

    /// <summary>Every registered product definition.</summary>
    public static IReadOnlyList<AgentProductDefinition> All { get; } = [Monica, MonicaWorkflow];
}
