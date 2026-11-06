using Monica.Core.Localization.Models;
using Monica.Core.Results;
using Monica.UI.UISystemInfo.Models;
using Monica.UI.UISystemInfo.State;

namespace Test.Monica.UI.UISystemInfo;

internal static class SystemInfoTestData
{
    internal static readonly DateTimeOffset CapturedAtUtc = new(2026, 8, 7, 2, 30, 45, TimeSpan.Zero);

    internal static SystemInfoSnapshot Snapshot(double? startupDurationMs = null) => new()
    {
        CapturedAtUtc = CapturedAtUtc,
        Application = new SystemInfoApplication
        {
            Name = "Flight Service",
            Id = "DEV-SERVICE-FLIGHT-API",
            Version = "v1.0.3",
            EnvironmentName = "Development"
        },
        Process = new SystemInfoProcess
        {
            StartedAtUtc = CapturedAtUtc.AddHours(-2),
            RuntimeVersion = ".NET 10.0.0",
            Architecture = "X64",
            ProcessId = 2126,
            WorkingSetBytes = 192 * 1024 * 1024,
            Is64BitOperatingSystem = true,
            Is64BitProcess = true,
            IsPrivileged = false,
            PageSizeBytes = 4096,
            IsInteractive = false,
            ProcessPath = @"D:\Services\FlightService.exe"
        },
        Host = new SystemInfoHost
        {
            MachineName = "DEV-WORKSTATION",
            UserName = "service-user",
            UserDomainName = "DEV",
            OperatingSystem = "Microsoft Windows 11",
            CurrentDirectory = @"D:\Services\FlightService",
            TimeZone = new SystemTimeZone
            {
                Id = "China Standard Time",
                UtcOffset = TimeSpan.FromHours(8)
            }
        },
        Artifact = new SystemInfoArtifact
        {
            ProductName = "Flight Service",
            ProductVersion = "1.0.3",
            FileVersion = "1.0.3.0",
            CompanyName = "Monica",
            LegalCopyright = "Copyright Monica",
            ArtifactTimestampUtc = CapturedAtUtc.AddDays(-1)
        },
        Startup = startupDurationMs is { } durationMs
            ? new SystemInfoApplicationStartup
            {
                StartedAtUtc = CapturedAtUtc.AddMilliseconds(-durationMs),
                ReadyAtUtc = CapturedAtUtc,
                DurationMs = durationMs
            }
            : null,
        Endpoints =
        [
            new SystemInfoEndpoint
            {
                Address = "https://localhost:7093",
                Scheme = "https"
            }
        ]
    };

    internal static SystemInfoCustomLink Link(
        string name,
        string url,
        int order = 0,
        SystemInfoCustomLinkTarget target = SystemInfoCustomLinkTarget.NewTab,
        bool enabled = true,
        string? userName = null,
        string? password = null) => new()
    {
        Name = LocalizedText.Plain(name),
        Url = url,
        Description = LocalizedText.Plain($"Open {name}"),
        Category = LocalizedText.Plain("Operations"),
        Order = order,
        Target = target,
        Enabled = enabled,
        UserName = userName,
        Password = password
    };

    internal static SystemInfoFacadeCalls Calls(
        Func<Result<SystemInfoSnapshot>>? getSnapshot = null,
        Func<Result>? requestSelfRestart = null) => new(
        getSnapshot ?? (() => Result.Success(Snapshot())),
        requestSelfRestart ?? (() => Result.Success()));
}
