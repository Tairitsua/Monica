using Microsoft.Extensions.Localization;
using Monica.Locker.Leases.Diagnostics;
using Monica.Locker.UI.Localization;

namespace Monica.Locker.UI.Support;

internal static class LeasePresentation
{
    public static string Role(LeaseAuthorityKind kind) => kind switch
    {
        LeaseAuthorityKind.Sql => "info", LeaseAuthorityKind.Redis => "third", _ => "neutral"
    };

    public static (string Value, string Unit) Duration(double seconds, IStringLocalizer<LockerResource> localizer)
    {
        var value = Math.Abs(seconds);
        return value < 60 ? (value.ToString("0"), localizer["Common:Seconds"])
            : value < 3600 ? ((value / 60).ToString("0.0"), localizer["Common:Minutes"])
            : ((value / 3600).ToString("0.0"), localizer["Common:Hours"]);
    }

    public static string Title(string name, IStringLocalizer<LockerResource> localizer) => name switch
    {
        "jobscheduler:maintenance" => localizer["Roles:Recovery"],
        _ when name.StartsWith("jobscheduler:owner:", StringComparison.Ordinal) => name["jobscheduler:owner:".Length..],
        _ when name.StartsWith("jobscheduler:history:", StringComparison.Ordinal) => localizer["Roles:History"],
        _ when name.StartsWith("outbox:maintenance:", StringComparison.Ordinal) => localizer["Roles:Outbox"],
        _ when name.StartsWith("service-discovery:", StringComparison.Ordinal) => localizer["Roles:Discovery"],
        _ => name
    };
}
