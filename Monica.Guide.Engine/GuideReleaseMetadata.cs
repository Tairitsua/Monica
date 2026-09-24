using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monica.Guide;

/// <summary>Loads and validates immutable release metadata without scanning skill directories.</summary>
public static class GuideReleaseMetadata
{
    private static readonly JsonSerializerOptions JSON_OPTIONS = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    /// <summary>
    /// Loads and validates immutable release metadata without scanning skill directories.
    /// Pass the expected product version to enforce bundle/runtime agreement, or null to
    /// validate an arbitrary candidate release on its own internal consistency. A product
    /// definition pins the release identity to one product's contract; null validates any
    /// product's bundle against its own declared shape.
    /// </summary>
    public static ReleaseObservation Observe(
        string applicationDirectory,
        string? expectedProductVersion,
        AgentProductDefinition? definition = null,
        string? expectedAssemblyVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        var manifestPath = ResolveManifestPath(applicationDirectory);
        if (!File.Exists(manifestPath))
        {
            return ReleaseObservation.Development;
        }

        var bytes = File.ReadAllBytes(manifestPath);
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(bytes, JSON_OPTIONS)
                       ?? throw new InvalidDataException("The release manifest is empty.");
        var bundleRoot = Path.GetDirectoryName(manifestPath)!;
        ValidateManifest(bundleRoot, manifest, expectedProductVersion, definition, expectedAssemblyVersion);
        return new ReleaseObservation(
            manifest.DistributionKind,
            Sha256(bytes),
            manifest);
    }

    internal static string ResolveManifestPath(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var manifestPath = Path.Combine(root, "release-manifest.json");
        var directoryName = Path.GetFileName(root);
        if (!File.Exists(manifestPath)
            && (string.Equals(directoryName, "app", StringComparison.OrdinalIgnoreCase)
                || string.Equals(directoryName, "setup", StringComparison.OrdinalIgnoreCase)))
        {
            manifestPath = Path.Combine(Directory.GetParent(root)?.FullName ?? root, "release-manifest.json");
        }

        return manifestPath;
    }

    public static SkillCatalog LoadSkillCatalog(
        string skillsRoot,
        string catalogRelativePath = "catalog.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogRelativePath);
        var root = Path.GetFullPath(skillsRoot);
        var catalogPath = ContainedPath(root, catalogRelativePath);
        var catalog = JsonSerializer.Deserialize<SkillCatalog>(
                          File.ReadAllBytes(catalogPath),
                          JSON_OPTIONS)
                      ?? throw new InvalidDataException("The skill catalog is empty.");
        ValidateCatalog(root, catalog);
        return catalog;
    }

    public static IReadOnlyList<SkillCatalogFile> EnumerateCatalogFiles(
        string skillsRoot,
        SkillCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillsRoot);
        ArgumentNullException.ThrowIfNull(catalog);
        var root = Path.GetFullPath(skillsRoot);
        return catalog.Skills
            .SelectMany(skill => skill.Files.Select(file => new SkillCatalogFile(
                skill.Name,
                file,
                ContainedPath(ResolveSkillRoot(root, skill), file))))
            .ToArray();
    }

    /// <summary>
    /// Enforces version consistency. A non-null expectation requires the manifest to name that
    /// exact product (and, when supplied, the running CLR assembly); a null expectation
    /// validates a candidate release on its own version agreement without pinning it to a
    /// process.
    /// </summary>
    public static void ValidateVersionProjection(
        ReleaseManifest manifest,
        string? expectedProductVersion,
        string? expectedAssemblyVersion = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.ProductVersion);
        if (expectedProductVersion is not null
            && !string.Equals(SemVerCore(manifest.ProductVersion), SemVerCore(expectedProductVersion), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Release manifest version '{manifest.ProductVersion}' does not match the expected product '{expectedProductVersion}'.");
        }
        if (manifest.McpVersion is not null
            && !string.Equals(manifest.McpVersion, manifest.ProductVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Release MCP version '{manifest.McpVersion}' does not match product version '{manifest.ProductVersion}'.");
        }
        if (expectedProductVersion is not null
            && expectedAssemblyVersion is not null
            && !string.Equals(manifest.AssemblyVersion, expectedAssemblyVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Release CLR assembly version '{manifest.AssemblyVersion}' does not match the running AssemblyName.Version "
                + $"'{expectedAssemblyVersion}'.");
        }
    }

    /// <summary>
    /// Strips SemVer build metadata (everything from <c>+</c>) so an informational version that
    /// appends the source revision still equals the manifest's pure release identity.
    /// </summary>
    internal static string SemVerCore(string version)
    {
        var metadata = version.IndexOf('+');
        return metadata < 0 ? version : version[..metadata];
    }

    private static void ValidateManifest(
        string bundleRoot,
        ReleaseManifest manifest,
        string? expectedProductVersion,
        AgentProductDefinition? definition,
        string? expectedAssemblyVersion)
    {
        if (manifest.SchemaVersion != ReleaseManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported release manifest schema {manifest.SchemaVersion}.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.ProductId);
        if (definition is not null
            && !string.Equals(manifest.ProductId, definition.ProductId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unexpected release product ID '{manifest.ProductId}'; this guide manages '{definition.ProductId}'.");
        }

        ValidateVersionProjection(manifest, expectedProductVersion, expectedAssemblyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.RuntimeIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.DistributionKind);
        ArgumentNullException.ThrowIfNull(manifest.Files);
        ArgumentNullException.ThrowIfNull(manifest.Skills);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.SourceCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.SkillCatalogDigest);

        var duplicate = manifest.Files
            .GroupBy(static file => file.RelativePath, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Release manifest contains duplicate file '{duplicate.Key}'.");
        }

        ValidateProductPins(manifest, definition);
        ValidateBundleProjection(bundleRoot, manifest, definition);
    }

    /// <summary>Pins a manifest's declared identity to one product's contract.</summary>
    private static void ValidateProductPins(ReleaseManifest manifest, AgentProductDefinition? definition)
    {
        if (definition is null)
        {
            return;
        }

        // Self-contained true is the legacy publish shape; both it and the current
        // framework-dependent shape stay valid so previously installed releases keep
        // working as rollback candidates.
        var sdkSegments = manifest.DotnetSdk.Split('.');
        var expectedRequiredRuntime = $"{sdkSegments[0]}.{sdkSegments[1]}";
        var platform = definition.PlatformFor(manifest.RuntimeIdentifier);
        if (platform is null)
        {
            throw new InvalidDataException(
                $"Release runtime identifier '{manifest.RuntimeIdentifier}' is not a published {definition.ProductName} platform.");
        }
        if (manifest.ProductName != definition.ProductName
            || manifest.Tag != $"v{manifest.ProductVersion}"
            || (definition.DotnetSdk is not null
                && !manifest.DotnetSdk.StartsWith($"{definition.DotnetSdk}.", StringComparison.Ordinal))
            || manifest.DistributionKind != platform.DistributionKind
            || manifest.SelfContained is null
            || (manifest.SelfContained is false
                && !string.Equals(manifest.RequiredRuntime, expectedRequiredRuntime, StringComparison.Ordinal))
            || manifest.Trimmed is not false
            || manifest.PublishSingleFile is not false)
        {
            throw new InvalidDataException(
                $"Release identity or publish properties differ from the {definition.ProductName} product contract.");
        }

        if (definition.SupportedRoutes is not null
            && !manifest.SupportedRoutes.SequenceEqual(definition.SupportedRoutes))
        {
            throw new InvalidDataException("Release routes differ from the product contract.");
        }
        if (definition.DefaultJourneys is not null
            && !manifest.DefaultJourneys.OrderBy(static item => item.Key, StringComparer.Ordinal).SequenceEqual(
                definition.DefaultJourneys.OrderBy(static item => item.Key, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Release default skill journeys differ from the product contract.");
        }
        if (definition.McpServerName is not null || definition.McpPath is not null)
        {
            if (manifest.McpServerName != definition.McpServerName
                || manifest.McpPath != definition.McpPath)
            {
                throw new InvalidDataException("Release MCP identity does not match the product contract.");
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(manifest.DoctorReadOnlyTool);
            ArgumentException.ThrowIfNullOrWhiteSpace(manifest.McpToolSchemaDigest);
        }
    }

    private static void ValidateBundleProjection(
        string bundleRoot,
        ReleaseManifest manifest,
        AgentProductDefinition? definition)
    {
        ValidateNoRedirectedEntries(bundleRoot);
        ValidateManifestFiles(bundleRoot, manifest.Files);
        var expectedFiles = manifest.Files.Select(static file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        var actualFiles = Directory.EnumerateFiles(bundleRoot, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(bundleRoot, file).Replace('\\', '/'))
            .Where(static file => file != "release-manifest.json")
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedFiles.SetEquals(actualFiles))
        {
            var missing = expectedFiles.Except(actualFiles, StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault();
            var extra = actualFiles.Except(expectedFiles, StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault();
            throw new InvalidDataException(
                $"Release payload does not exactly match manifest files (missing: {missing ?? "none"}; extra: {extra ?? "none"}).");
        }

        // The declared entry point, when one exists, must be present and be the exact
        // program-file subset of the manifest; its file name is platform-specific.
        var entry = definition?.ProgramEntryPointFor(manifest.RuntimeIdentifier) ?? manifest.ProgramEntryPoint;
        if (entry is not null)
        {
            var programDirectory = entry.Split('/', 2)[0];
            var expectedProgramFiles = manifest.Files
                .Where(static file => file.RelativePath.Contains('/', StringComparison.Ordinal))
                .Where(file => file.RelativePath.StartsWith(programDirectory + "/", StringComparison.Ordinal))
                .ToArray();
            if (!manifest.ProgramFiles.SequenceEqual(expectedProgramFiles))
            {
                throw new InvalidDataException(
                    $"Release programFiles is not the exact {programDirectory}/ subset of manifest files.");
            }
            if (manifest.ProgramEntryPoint != entry
                || !File.Exists(Path.Combine(bundleRoot, entry.Replace('/', Path.DirectorySeparatorChar))))
            {
                throw new InvalidDataException($"Release entry point must be {entry}.");
            }
        }
        else if (manifest.ProgramFiles.Count > 0)
        {
            throw new InvalidDataException("A skill-only release must not declare program files.");
        }

        if (!manifest.SupportedHosts.Order(StringComparer.Ordinal).SequenceEqual(
                (definition?.SupportedHosts ?? manifest.SupportedHosts).Order(StringComparer.Ordinal)))
        {
            throw new InvalidDataException(
                definition is null
                    ? "Release supportedHosts contains duplicates."
                    : "Release supportedHosts differ from the product contract.");
        }
        if (definition is not null
            && !manifest.DefaultRoutes.OrderBy(static item => item.Key, StringComparer.Ordinal).SequenceEqual(
                definition.DefaultRoutes.OrderBy(static item => item.Key, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Release defaultRoutes differ from the product contract.");
        }

        var skillsRoot = Path.Combine(bundleRoot, "skills");
        var catalogPath = Path.Combine(skillsRoot, "catalog.json");
        var catalogBytes = File.ReadAllBytes(catalogPath);
        var catalog = LoadSkillCatalog(skillsRoot);
        var catalogDigest = Sha256(catalogBytes);
        if (manifest.SkillCatalogDigest != catalogDigest)
        {
            throw new InvalidDataException("Release skill catalog digest does not match packaged skills/catalog.json.");
        }
        // Legacy self-contained manifests also project a skillsAsset block; keep it
        // validated when present so rollback candidates are verified in full.
        if (manifest.SkillsAsset is not null
            && (manifest.SkillsAsset.CatalogPath != "skills/catalog.json"
                || manifest.SkillsAsset.CatalogSha256 != catalogDigest
                || manifest.SkillsAsset.TreeDigest != catalog.TreeDigest
                || manifest.SkillsAsset.SkillCount != catalog.SkillCount
                || manifest.SkillsAsset.AssetName
                    != $"{definition?.BundleDirectoryPrefix ?? "monica-workflow-"}agent-bundle-v{manifest.ProductVersion}.zip"))
        {
            throw new InvalidDataException("Release skill catalog projection does not match packaged skills/catalog.json.");
        }
        var catalogSkills = catalog.Skills.Select(static skill => new
        {
            skill.Name,
            RelativePath = skill.Path,
            TreeDigest = skill.TreeDigest,
            skill.Files,
            skill.Dependencies
        }).ToArray();
        var manifestSkills = manifest.Skills.Select(static skill => new
        {
            skill.Name,
            RelativePath = skill.RelativePath,
            TreeDigest = skill.TreeDigest ?? skill.Sha256 ?? string.Empty,
            Files = skill.Files ?? [],
            Dependencies = skill.Dependencies ?? []
        }).ToArray();
        if (!JsonSerializer.SerializeToUtf8Bytes(catalogSkills, JSON_OPTIONS)
                .AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(manifestSkills, JSON_OPTIONS))
            || (manifest.SkillsAsset is not null
                && !JsonSerializer.SerializeToUtf8Bytes(manifest.SkillsAsset.Skills, JSON_OPTIONS)
                    .AsSpan()
                    .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(manifest.Skills, JSON_OPTIONS))))
        {
            throw new InvalidDataException("Release skill projection does not exactly match the packaged catalog.");
        }
    }

    private static void ValidateManifestFiles(string bundleRoot, IReadOnlyList<ReleaseFile> files)
    {
        foreach (var file in files)
        {
            var path = ContainedPath(bundleRoot, file.RelativePath);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"Release manifest file is missing: {file.RelativePath}");
            }

            var info = new FileInfo(path);
            if (info.Length != file.Length
                || !string.Equals(Sha256(File.ReadAllBytes(path)), file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Release manifest file digest mismatch: {file.RelativePath}");
            }
        }
    }

    private static void ValidateNoRedirectedEntries(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Release payload contains a redirected directory: {directory}");
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Release payload contains a redirected entry: {entry}");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void ValidateCatalog(string root, SkillCatalog catalog)
    {
        if (catalog.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported skill catalog schema {catalog.SchemaVersion}.");
        }

        if (catalog.SkillCount != catalog.Skills.Count)
        {
            throw new InvalidDataException("The skill catalog count does not match its entries.");
        }

        EnsureOrdinal(catalog.Skills.Select(static skill => skill.Name), "skill entries");
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var skill in catalog.Skills)
        {
            EnsureOrdinal(skill.Files, $"files for skill '{skill.Name}'");
            EnsureOrdinal(skill.Dependencies, $"dependencies for skill '{skill.Name}'");
            if (skill.Profiles is { Count: > 0 })
            {
                EnsureOrdinal(skill.Profiles, $"profiles for skill '{skill.Name}'");
            }
            var skillRoot = ResolveSkillRoot(root, skill);
            using var tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var relativeFile in skill.Files)
            {
                AppendUtf8(tree, relativeFile);
                tree.AppendData([0]);
                tree.AppendData(File.ReadAllBytes(ContainedPath(skillRoot, relativeFile)));
                tree.AppendData([0]);
            }

            var treeDigest = Convert.ToHexString(tree.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(treeDigest, skill.TreeDigest, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Skill '{skill.Name}' tree digest does not match its files.");
            }

            AppendUtf8(aggregate, skill.Name);
            aggregate.AppendData([0]);
            AppendUtf8(aggregate, skill.TreeDigest);
            aggregate.AppendData([0]);
        }

        var aggregateDigest = Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(aggregateDigest, catalog.TreeDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The skill catalog aggregate tree digest does not match its entries.");
        }

        ValidateWorkspaceProjection(catalog);
    }

    /// <summary>
    /// Validates the optional workspace-facing catalog projection. Profile templates must
    /// reference packaged skills and match the packaged profile closure; source repositories
    /// and retired aliases must stay internally consistent.
    /// </summary>
    private static void ValidateWorkspaceProjection(SkillCatalog catalog)
    {
        if (catalog.ManagedInstructions is null)
        {
            return;
        }

        var instructions = catalog.ManagedInstructions;
        if (instructions.Version < 1
            || string.IsNullOrWhiteSpace(instructions.Markers.Start)
            || string.IsNullOrWhiteSpace(instructions.Markers.End)
            || string.Equals(instructions.Markers.Start, instructions.Markers.End, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Managed instruction markers are invalid.");
        }

        var packaged = catalog.Skills.Select(static skill => skill.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (profile, template) in instructions.Templates)
        {
            if (string.IsNullOrWhiteSpace(profile)
                || template.Skills.Count == 0
                || template.Skills.Any(string.IsNullOrWhiteSpace)
                || template.Rules.Count == 0
                || template.Rules.Any(static rule =>
                    string.IsNullOrWhiteSpace(rule.Id)
                    || string.IsNullOrWhiteSpace(rule.Text)))
            {
                throw new InvalidDataException(
                    $"Managed instruction template '{profile}' must list skills and rules.");
            }

            var duplicateRuleIds = template.Rules
                .GroupBy(static rule => rule.Id, StringComparer.Ordinal)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToArray();
            if (duplicateRuleIds.Length > 0)
            {
                throw new InvalidDataException(
                    $"Managed instruction template '{profile}' repeats rule ids: {string.Join(", ", duplicateRuleIds)}.");
            }

            var missing = template.Skills.Where(skill => !packaged.Contains(skill)).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidDataException(
                    $"Managed instruction template '{profile}' references unpackaged skills: {string.Join(", ", missing)}.");
            }
        }

        if (catalog.SourceRepositories is not null)
        {
            foreach (var repository in catalog.SourceRepositories.Values)
            {
                if (string.IsNullOrWhiteSpace(repository.Repository)
                    || repository.Aliases.Count == 0
                    || repository.Aliases.Any(static alias => string.IsNullOrWhiteSpace(alias)))
                {
                    throw new InvalidDataException(
                        $"Source repository '{repository.Repository}' must declare its own name and aliases.");
                }
            }
        }

        if (catalog.Aliases is not null)
        {
            foreach (var (name, alias) in catalog.Aliases)
            {
                if (string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(alias.Canonical)
                    || !packaged.Contains(alias.Canonical))
                {
                    throw new InvalidDataException($"Skill alias '{name}' must map to a packaged skill.");
                }
            }
        }
    }

    private static void EnsureOrdinal(IEnumerable<string> values, string description)
    {
        var materialized = values.ToArray();
        if (!materialized.SequenceEqual(materialized.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Skill catalog {description} must use ordinal order.");
        }

        if (materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Length)
        {
            throw new InvalidDataException($"Skill catalog {description} contains duplicates.");
        }
    }

    private static string ContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Catalog path must be relative: {relativePath}");
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)
            && !string.Equals(candidate, normalizedRoot, comparison))
        {
            throw new InvalidDataException($"Catalog path escapes the skills root: {relativePath}");
        }

        return candidate;
    }

    private static string ResolveSkillRoot(string root, SkillCatalogEntry skill)
    {
        var authoringPath = ContainedPath(root, skill.Path);
        if (Directory.Exists(authoringPath))
        {
            return authoringPath;
        }

        // The packaged catalog intentionally retains its canonical repository path while the
        // immutable asset projects each entry directly to skills/<name>.
        var packagedPath = ContainedPath(root, skill.Name);
        return Directory.Exists(packagedPath) ? packagedPath : authoringPath;
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
        => hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static string Sha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>Release metadata observed beside the running executable.</summary>
public sealed record ReleaseObservation(
    string DistributionKind,
    string ManifestDigest,
    ReleaseManifest? Manifest)
{
    public static ReleaseObservation Development { get; } = new(
        "source",
        "development",
        null);
}

/// <summary>Exact source file authorized by a validated packaged catalog.</summary>
public sealed record SkillCatalogFile(
    string SkillName,
    string RelativePath,
    string SourcePath);
