using System.Collections.Immutable;

namespace Monica.UI.UISystemInfo.Models;

/// <summary>
/// Represents one immutable, point-in-time view of the current Monica host.
/// </summary>
public sealed record SystemInfoSnapshot
{
    /// <summary>Gets the UTC instant at which the snapshot was captured.</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Gets the host-owned application identity.</summary>
    public required SystemInfoApplication Application { get; init; }

    /// <summary>Gets process and runtime evidence captured for the current host process.</summary>
    public required SystemInfoProcess Process { get; init; }

    /// <summary>Gets operating-system and host-environment evidence.</summary>
    public required SystemInfoHost Host { get; init; }

    /// <summary>Gets metadata for the executable artifact that hosts the application.</summary>
    public required SystemInfoArtifact Artifact { get; init; }

    /// <summary>
    /// Gets the opt-in application startup interval, or <see langword="null"/> when tracking was not enabled or the
    /// Generic Host has not published <c>ApplicationStarted</c>.
    /// </summary>
    public SystemInfoApplicationStartup? Startup { get; init; }

    /// <summary>Gets the normalized addresses currently published by the ASP.NET Core server.</summary>
    public required ImmutableArray<SystemInfoEndpoint> Endpoints { get; init; }
}

/// <summary>
/// Represents the application identity shared with the Monica shell.
/// </summary>
public sealed record SystemInfoApplication
{
    /// <summary>Gets the application display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the application identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the application version displayed by the Monica shell.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the Generic Host environment name.</summary>
    public required string EnvironmentName { get; init; }
}

/// <summary>
/// Represents process and managed-runtime evidence captured from the current process.
/// </summary>
public sealed record SystemInfoProcess
{
    /// <summary>Gets the UTC instant at which the current process started.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Gets the managed runtime description.</summary>
    public required string RuntimeVersion { get; init; }

    /// <summary>Gets the current process architecture.</summary>
    public required string Architecture { get; init; }

    /// <summary>Gets the current process identifier.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Gets the process working-set size at capture time, in bytes.</summary>
    public required long WorkingSetBytes { get; init; }

    /// <summary>Gets whether the operating system is 64-bit.</summary>
    public required bool Is64BitOperatingSystem { get; init; }

    /// <summary>Gets whether the current process is 64-bit.</summary>
    public required bool Is64BitProcess { get; init; }

    /// <summary>Gets whether the current process has elevated operating-system privileges.</summary>
    public required bool IsPrivileged { get; init; }

    /// <summary>Gets the operating-system page size in bytes.</summary>
    public required int PageSizeBytes { get; init; }

    /// <summary>Gets whether the current process is running in an interactive user session.</summary>
    public required bool IsInteractive { get; init; }

    /// <summary>Gets the current executable path, when the runtime exposes it.</summary>
    public string? ProcessPath { get; init; }
}

/// <summary>
/// Represents operating-system and Generic Host evidence.
/// </summary>
public sealed record SystemInfoHost
{
    /// <summary>Gets the machine name.</summary>
    public required string MachineName { get; init; }

    /// <summary>Gets the current operating-system user name.</summary>
    public required string UserName { get; init; }

    /// <summary>Gets the current operating-system user domain.</summary>
    public required string UserDomainName { get; init; }

    /// <summary>Gets the operating-system description.</summary>
    public required string OperatingSystem { get; init; }

    /// <summary>Gets the process working directory at capture time.</summary>
    public required string CurrentDirectory { get; init; }

    /// <summary>Gets the configured Clock display timezone and its UTC offset at capture time.</summary>
    public required SystemTimeZone TimeZone { get; init; }
}

/// <summary>
/// Represents metadata read from the executable artifact that hosts the application.
/// </summary>
public sealed record SystemInfoArtifact
{
    /// <summary>Gets the product name stored in the artifact metadata, when available.</summary>
    public string? ProductName { get; init; }

    /// <summary>Gets the product version stored in the artifact metadata, when available.</summary>
    public string? ProductVersion { get; init; }

    /// <summary>Gets the file version stored in the artifact metadata, when available.</summary>
    public string? FileVersion { get; init; }

    /// <summary>Gets the company name stored in the artifact metadata, when available.</summary>
    public string? CompanyName { get; init; }

    /// <summary>Gets the legal copyright stored in the artifact metadata, when available.</summary>
    public string? LegalCopyright { get; init; }

    /// <summary>
    /// Gets the executable artifact's last-write timestamp in UTC, or <see langword="null"/> when no physical
    /// artifact is available.
    /// </summary>
    public DateTimeOffset? ArtifactTimestampUtc { get; init; }
}

/// <summary>
/// Represents the completed, opt-in interval from the application-owned startup marker until the Generic Host became
/// ready.
/// </summary>
public sealed record SystemInfoApplicationStartup
{
    /// <summary>Gets the UTC instant paired with the application-owned startup marker.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Gets the UTC instant at which the Generic Host published <c>ApplicationStarted</c>.</summary>
    public required DateTimeOffset ReadyAtUtc { get; init; }

    /// <summary>Gets the monotonic startup duration in milliseconds.</summary>
    public required double DurationMs { get; init; }
}

/// <summary>
/// Represents one normalized listening address published by the ASP.NET Core server.
/// </summary>
public sealed record SystemInfoEndpoint
{
    /// <summary>Gets the exact normalized listening address.</summary>
    public required string Address { get; init; }

    /// <summary>Gets the transport scheme parsed from <see cref="Address"/>, or an empty value when absent.</summary>
    public required string Scheme { get; init; }
}
