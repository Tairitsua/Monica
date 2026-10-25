using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.Modularity.Models;
using Monica.Modules;
using Monica.UI.UISystemInfo.Models;
using Monica.UI.Shared.Services;

namespace Monica.UI.UISystemInfo.Services;

internal enum SystemInfoUnavailableReason
{
    BrowserRuntime,
    EntryAssemblyUnavailable
}

internal sealed class SystemInfoUnavailableException(
    SystemInfoUnavailableReason reason,
    string message) : InvalidOperationException(message)
{
    public SystemInfoUnavailableReason Reason { get; } = reason;
}

internal sealed class SystemInfoSnapshotProvider(
    IServer server,
    MonicaApplication monicaApplication,
    IOptions<ModuleShellUIOption> shellOptions,
    MoTimeFormatter timeFormatter,
    IHostEnvironment hostEnvironment)
{
    internal SystemInfoSnapshot Capture()
    {
        if (OperatingSystem.IsBrowser())
        {
            throw new SystemInfoUnavailableException(
                SystemInfoUnavailableReason.BrowserRuntime,
                "System information cannot be captured in a browser runtime.");
        }

        var entryAssembly = Assembly.GetEntryAssembly()
                            ?? throw new SystemInfoUnavailableException(
                                SystemInfoUnavailableReason.EntryAssemblyUnavailable,
                                "The entry assembly is unavailable.");

        var capturedAtUtc = DateTimeOffset.UtcNow;
        using var process = Process.GetCurrentProcess();
        var artifactPath = ResolveArtifactPath(entryAssembly);
        var artifactVersion = artifactPath is null ? null : FileVersionInfo.GetVersionInfo(artifactPath);
        var startup = CaptureCompletedStartup(monicaApplication.StartupTiming);
        var identity = shellOptions.Value;

        return new SystemInfoSnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            Application = new SystemInfoApplication
            {
                Name = identity.GetAppName(),
                Id = identity.GetAppId(),
                Version = identity.GetAppVersion(),
                EnvironmentName = hostEnvironment.EnvironmentName
            },
            Process = new SystemInfoProcess
            {
                StartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime()),
                RuntimeVersion = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessId = process.Id,
                WorkingSetBytes = process.WorkingSet64,
                Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
                Is64BitProcess = Environment.Is64BitProcess,
                IsPrivileged = Environment.IsPrivilegedProcess,
                PageSizeBytes = Environment.SystemPageSize,
                IsInteractive = Environment.UserInteractive,
                ProcessPath = Environment.ProcessPath
            },
            Host = new SystemInfoHost
            {
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                UserDomainName = Environment.UserDomainName,
                OperatingSystem = RuntimeInformation.OSDescription,
                CurrentDirectory = Environment.CurrentDirectory,
                TimeZone = new SystemTimeZone
                {
                    Id = timeFormatter.TimeZone.Id,
                    UtcOffset = timeFormatter.TimeZone.GetUtcOffset(capturedAtUtc)
                }
            },
            Artifact = new SystemInfoArtifact
            {
                ProductName = artifactVersion?.ProductName ?? entryAssembly.GetName().Name,
                ProductVersion = artifactVersion?.ProductVersion,
                FileVersion = artifactVersion?.FileVersion,
                CompanyName = artifactVersion?.CompanyName,
                LegalCopyright = artifactVersion?.LegalCopyright,
                ArtifactTimestampUtc = artifactPath is null
                    ? null
                    : new DateTimeOffset(File.GetLastWriteTimeUtc(artifactPath))
            },
            Startup = startup,
            Endpoints = CaptureEndpoints(
                server.Features.Get<IServerAddressesFeature>()?.Addresses)
        };
    }

    private static string? ResolveArtifactPath(Assembly entryAssembly)
    {
        if (!string.IsNullOrWhiteSpace(entryAssembly.Location) && File.Exists(entryAssembly.Location))
        {
            return entryAssembly.Location;
        }

        return !string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath)
            ? Environment.ProcessPath
            : null;
    }

    private static SystemInfoApplicationStartup? CaptureCompletedStartup(MonicaStartupTiming? timing)
    {
        if (timing is not { IsReady: true, ReadyAtUtc: { } readyAtUtc, DurationMs: { } durationMs })
        {
            return null;
        }

        return new SystemInfoApplicationStartup
        {
            StartedAtUtc = timing.StartedAtUtc,
            ReadyAtUtc = readyAtUtc,
            DurationMs = durationMs
        };
    }

    private static ImmutableArray<SystemInfoEndpoint> CaptureEndpoints(ICollection<string>? publishedAddresses)
    {
        if (publishedAddresses is null || publishedAddresses.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = ImmutableArray.CreateBuilder<SystemInfoEndpoint>(publishedAddresses.Count);

        foreach (var publishedAddress in publishedAddresses)
        {
            if (string.IsNullOrWhiteSpace(publishedAddress))
            {
                continue;
            }

            var address = publishedAddress.Trim();
            if (!seen.Add(address))
            {
                continue;
            }

            endpoints.Add(new SystemInfoEndpoint
            {
                Address = address,
                Scheme = ParseScheme(address)
            });
        }

        return endpoints.ToImmutable();
    }

    private static string ParseScheme(string address)
    {
        var separatorIndex = address.IndexOf("://", StringComparison.Ordinal);
        return separatorIndex > 0 ? address[..separatorIndex].ToLowerInvariant() : string.Empty;
    }
}
