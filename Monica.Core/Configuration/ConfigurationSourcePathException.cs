namespace Monica.Core.Configuration;

/// <summary>Rejects a configuration-source contribution using a path-only, value-free diagnostic.</summary>
/// <remarks>Pass the configuration path owned by a contract, never its configured value.</remarks>
public sealed class ConfigurationSourcePathException(string path)
    : InvalidOperationException($"Configuration path '{path}' is not permitted in this source.")
{
    /// <summary>Gets the rejected configuration path.</summary>
    public string Path { get; } = path;
}
