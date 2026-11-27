namespace Monica.DevOps.FileOps.Models;

public class FileOpsRuntimeConfig
{
    public List<string> AllowedRoots { get; set; } = [];

    public List<string> ReadOnlyPaths { get; set; } = [];

    public string StartupPath { get; set; } = string.Empty;

    public bool AllowWriteOperations { get; set; } = true;

    public bool AllowDeleteOperations { get; set; }

    public bool AllowHiddenEntries { get; set; }

    public long MaxTextFileSizeBytes { get; set; } = 512 * 1024;

    public long MaxUploadFileSizeBytes { get; set; } = 20 * 1024 * 1024;

    public long MaxDownloadFileSizeBytes { get; set; } = 100 * 1024 * 1024;

    public int MaxDirectoryEntries { get; set; } = 500;

    public List<string> EditableExtensions { get; set; } =
    [
        ".json",
        ".yaml",
        ".yml",
        ".txt",
        ".log",
        ".config",
        ".conf",
        ".cfg",
        ".ini",
        ".xml",
        ".props",
        ".targets",
        ".csproj",
        ".sh",
        ".ps1",
        ".cmd",
        ".bat",
        ".env"
    ];

    public FileOpsRuntimeConfig Clone()
    {
        return new FileOpsRuntimeConfig
        {
            AllowedRoots = [.. AllowedRoots],
            ReadOnlyPaths = [.. ReadOnlyPaths],
            StartupPath = StartupPath,
            AllowWriteOperations = AllowWriteOperations,
            AllowDeleteOperations = AllowDeleteOperations,
            AllowHiddenEntries = AllowHiddenEntries,
            MaxTextFileSizeBytes = MaxTextFileSizeBytes,
            MaxUploadFileSizeBytes = MaxUploadFileSizeBytes,
            MaxDownloadFileSizeBytes = MaxDownloadFileSizeBytes,
            MaxDirectoryEntries = MaxDirectoryEntries,
            EditableExtensions = [.. EditableExtensions]
        };
    }

    /// <summary>Normalizes paths, limits and extensions for the current host and returns this instance.</summary>
    public FileOpsRuntimeConfig Normalize()
    {
        NormalizeWithEvidence();
        return this;
    }

    internal FileOpsPolicyNormalization NormalizeWithEvidence()
    {
        AllowedRoots = NormalizePaths(AllowedRoots);
        var usedDefaultRoots = AllowedRoots.Count == 0;
        if (AllowedRoots.Count == 0)
        {
            AllowedRoots = GetDefaultAllowedRoots();
        }

        var readOnlyPaths = NormalizePaths(ReadOnlyPaths)
            .ToLookup(path => AllowedRoots.Any(root => IsWithinRoot(path, root)));
        ReadOnlyPaths = readOnlyPaths[true].ToList();

        EditableExtensions = EditableExtensions
            .Select(NormalizeExtension)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(GetPathComparer())
            .ToList();

        MaxTextFileSizeBytes = Math.Max(MaxTextFileSizeBytes, 4 * 1024);
        MaxUploadFileSizeBytes = Math.Max(MaxUploadFileSizeBytes, 64 * 1024);
        MaxDownloadFileSizeBytes = Math.Max(MaxDownloadFileSizeBytes, MaxUploadFileSizeBytes);
        MaxDirectoryEntries = Math.Clamp(MaxDirectoryEntries, 50, 5_000);

        StartupPath = NormalizePath(StartupPath);
        var usedDefaultStartup = string.IsNullOrWhiteSpace(StartupPath) ||
            !AllowedRoots.Any(root => IsWithinRoot(StartupPath, root)) ||
            !Directory.Exists(StartupPath);
        if (usedDefaultStartup)
        {
            StartupPath = AllowedRoots[0];
        }

        return new(usedDefaultRoots, usedDefaultStartup, readOnlyPaths[false].ToArray());
    }

    public bool CanEditExtension(string? extension)
    {
        extension = NormalizeExtension(extension);
        return EditableExtensions.Contains(extension, GetPathComparer());
    }

    public static List<string> ParseMultiValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return [];
        }

        return rawValue
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(GetPathComparer())
            .ToList();
    }

    public static string ToMultilineText(IEnumerable<string>? values)
    {
        return values == null ? string.Empty : string.Join(Environment.NewLine, values);
    }

    private static List<string> GetDefaultAllowedRoots()
    {
        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetTempPath()
        };

        return NormalizePaths(candidates);
    }

    private static List<string> NormalizePaths(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Select(NormalizePath)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(GetPathComparer())
            .ToList();
    }

    internal static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim().Trim('"')));
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string NormalizeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (!normalized.StartsWith('.'))
        {
            normalized = $".{normalized}";
        }

        return normalized.ToLowerInvariant();
    }

    internal static bool IsWithinRoot(string path, string rootPath)
    {
        var comparison = GetPathComparison();
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath);
        if (string.Equals(normalizedPath, normalizedRoot, comparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(normalizedRoot) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, comparison);
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}

internal sealed record FileOpsPolicyNormalization(bool UsedDefaultRoots, bool UsedDefaultStartup, IReadOnlyList<string> DiscardedReadOnlyPaths);
