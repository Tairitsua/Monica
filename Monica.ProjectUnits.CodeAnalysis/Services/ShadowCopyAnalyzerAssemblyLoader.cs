using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;

namespace Monica.ProjectUnits.CodeAnalysis.Services;

/// <summary>
/// Loads analyzer and source-generator assemblies from per-process shadow copies, so workspace
/// analysis never locks the analyzed repository's build outputs. Roslyn's default loader maps the
/// original analyzer files and keeps them write-blocked for the process lifetime, which blocks
/// every later rebuild of the projects producing those assemblies (MSB3021/3027). Loading stays
/// path-based and file-backed exactly like the default loader — only the physical path differs.
/// </summary>
public sealed class ShadowCopyAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
{
    private static readonly HostServices HOST_SERVICES = MefHostServices.Create(
        MSBuildMefHostServices.DefaultAssemblies.Add(typeof(ShadowCopyAnalyzerAssemblyLoader).Assembly));

    private readonly string _shadowRoot = Path.Combine(
        Path.GetTempPath(),
        "monica-analyzer-shadow",
        Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string, string> _dependencyDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Assembly> _assembliesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ShadowAssemblyLoadContext _context;

    public ShadowCopyAnalyzerAssemblyLoader()
        => _context = new ShadowAssemblyLoadContext(this);

    /// <inheritdoc />
    public void AddDependencyLocation(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        _ = _dependencyDirectories[Path.GetDirectoryName(Path.GetFullPath(fullPath))!] = string.Empty;
    }

    /// <inheritdoc />
    public Assembly LoadFromPath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (_assembliesByPath.TryGetValue(fullPath, out var cached))
        {
            return cached;
        }

        // The load context resolves a name by simple name once loaded, so a rare duplicate load
        // under a race converges on one assembly instance without any cross-call managed lock.
        // Holding a lock across a runtime load call would risk deadlocking against the runtime's
        // own loader state when it invokes Load on another thread.
        var assembly = _context.LoadFromAssemblyPath(ShadowCopy(Path.GetFullPath(fullPath)));
        _assembliesByPath[fullPath] = assembly;
        return assembly;
    }

    /// <summary>
    /// Creates an MSBuild workspace whose analyzer service loads analyzer and generator assemblies
    /// through shadow copies instead of Roslyn's original-file-mapping default.
    /// </summary>
    /// <param name="solutionPath">Optional absolute solution path supplying the design-time SolutionDir.</param>
    public static MSBuildWorkspace CreateWorkspace(string? solutionPath = null)
        => MSBuildWorkspace.Create(ProjectUnitBuildInputs.CreateGlobalProperties(solutionPath), HOST_SERVICES);

    private string ShadowCopy(string sourcePath)
    {
        var identity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath)))[..12].ToLowerInvariant();
        var target = Path.Combine(
            _shadowRoot,
            $"{Path.GetFileNameWithoutExtension(sourcePath)}-{identity}{Path.GetExtension(sourcePath)}");
        if (File.Exists(target))
        {
            return target;
        }

        Directory.CreateDirectory(_shadowRoot);
        try
        {
            File.Copy(sourcePath, target, overwrite: false);
        }
        catch (IOException) when (File.Exists(target))
        {
            // Another thread in this process won the copy race.
        }

        return target;
    }

    /// <summary>
    /// Resolves analyzer dependencies against the registered dependency locations through shadow
    /// copies; anything unresolved falls back to the default context, which supplies the host's
    /// own framework assemblies, including Microsoft.CodeAnalysis itself.
    /// </summary>
    private sealed class ShadowAssemblyLoadContext(ShadowCopyAnalyzerAssemblyLoader owner) : AssemblyLoadContext
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null)
            {
                return null;
            }

            foreach (var directory in owner._dependencyDirectories.Keys)
            {
                var candidate = Path.Combine(directory, $"{name.Name}.dll");
                if (File.Exists(candidate))
                {
                    return LoadFromAssemblyPath(owner.ShadowCopy(candidate));
                }
            }

            return null;
        }
    }
}

/// <summary>
/// Replaces Roslyn's default analyzer service in composed MSBuild workspace services so every
/// analyzer and source-generator assembly loads through <see cref="ShadowCopyAnalyzerAssemblyLoader"/>.
/// </summary>
[ExportWorkspaceService(typeof(IAnalyzerService), ServiceLayer.Host)]
[Shared]
public sealed class ShadowCopyAnalyzerService : IAnalyzerService
{
    private static readonly ShadowCopyAnalyzerAssemblyLoader SHARED_LOADER = new();

    /// <summary>One shared loader keeps a single Assembly instance per simple name, as Roslyn's default does.</summary>
    public IAnalyzerAssemblyLoader GetLoader() => SHARED_LOADER;
}
