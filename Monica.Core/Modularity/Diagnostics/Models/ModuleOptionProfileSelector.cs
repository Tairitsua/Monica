using Monica.Core.Modularity.Models;

namespace Monica.Core.Modularity.Diagnostics.Models;

/// <summary>
/// Identifies one module option and profile request without retaining its live option object.
/// </summary>
/// <param name="ModuleKey">The host-local module diagnostics key.</param>
/// <param name="ProfileSelector">The exact profile-selection and fallback behavior.</param>
public sealed record ModuleOptionDiagnosticsTarget(
    ModuleKey ModuleKey,
    ModuleOptionProfileSelector ProfileSelector);

/// <summary>Identifies which finalized module option instance diagnostics should project.</summary>
public sealed record ModuleOptionProfileSelector
{
    private ModuleOptionProfileSelector(ModuleOptionProfileSelectionMode mode, string? profileName)
    {
        Mode = mode;
        ProfileName = profileName;
    }

    /// <summary>Gets a selector for the module's default options.</summary>
    public static ModuleOptionProfileSelector Default { get; } = new(
        ModuleOptionProfileSelectionMode.Default,
        null);

    /// <summary>Gets the requested selection behavior.</summary>
    public ModuleOptionProfileSelectionMode Mode { get; }

    /// <summary>Gets the requested named profile.</summary>
    public string? ProfileName { get; }

    /// <summary>Creates a selector that fails when the requested named profile does not exist.</summary>
    /// <param name="profileName">The exact case-sensitive profile name.</param>
    public static ModuleOptionProfileSelector Named(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        return new ModuleOptionProfileSelector(ModuleOptionProfileSelectionMode.Named, profileName);
    }

    /// <summary>
    /// Creates a selector that uses the requested named profile when present and otherwise projects default options.
    /// This is intended for keyed services that may alias a module's shared default provider.
    /// </summary>
    /// <param name="profileName">
    /// An exact non-null profile name or service key. Empty and whitespace keys preserve their identity and resolve to the default options.
    /// </param>
    public static ModuleOptionProfileSelector NamedOrDefault(string profileName)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        return new ModuleOptionProfileSelector(ModuleOptionProfileSelectionMode.NamedOrDefault, profileName);
    }
}

/// <summary>Controls named-profile fallback behavior.</summary>
public enum ModuleOptionProfileSelectionMode
{
    /// <summary>Projects default options.</summary>
    Default,

    /// <summary>Requires the named profile to exist.</summary>
    Named,

    /// <summary>Uses a named profile when present and otherwise explicitly falls back to default options.</summary>
    NamedOrDefault
}
